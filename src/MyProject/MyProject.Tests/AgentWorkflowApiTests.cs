using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Ai;
using MyProject.Dtos.Auths;
using MyProject.Dtos.Commons;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using MyProject.Web.Ai;
using MyProject.Web.Ai.Workflows;
using MyProject.Web.Client.Api;

namespace MyProject.Tests;

/// <summary>
/// Agent 工作流程的 Web API（0.10.7 起，WASM 遷移 M8）：真的執行器與 MAF Workflows 跑在測試主機裡，只有上游模型換成劇本
/// （系統提示詞只放種類代碼，依它分給各 agent 的劇本）。驗證跨請求的執行登錄表：開始 → 停下來等使用者 → 重新整理後接回 → 回覆 → 完成；
/// 別人不能操作、閒置逾時結束並釋放 scope、停止、斷線視同離開頁面、規則檢查在伺服器、權限、稽核與記帳都以執行 scope 裡的使用者計算。
/// </summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class AgentWorkflowApiTests : IClassFixture<ApiTestApplicationFactoryWithScriptedWorkflowAi>
{
    private const string Password = "Passw0rd-1";
    private const string Base = "/api/v1/agent-workflows";
    private const string ReportWorkflow = "ProjectStatusReport";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ApiTestApplicationFactoryWithScriptedWorkflowAi factory;

    public AgentWorkflowApiTests(ApiTestApplicationFactoryWithScriptedWorkflowAi factory)
    {
        this.factory = factory;
        factory.Ai.Reset();
    }

    // ───────────────────────────── 頁面資訊與權限 ─────────────────────────────

    [Fact]
    public async Task Info_ShouldListTheWorkflowsAndTheRetention()
    {
        using var client = await SignedInClientAsync(await AddUserAsync());

        var info = await SendAsync<AgentWorkflowPageInfoDto>(client, HttpMethod.Get, $"{Base}/info");

        Assert.Null(info.UnavailableReason);
        Assert.True(info.RetentionDays > 0);
        Assert.Equal(AiWorkflowCatalog.All.Select(x => x.Name), info.Workflows.Select(x => x.Name));
        Assert.Equal([3, 8], info.Workflows.Select(x => x.InputRows));
        Assert.Equal(ProjectStatusReportWorkflow.Definition.InputPlaceholder, info.Workflows[0].InputPlaceholder);
    }

    [Fact]
    public async Task WithoutThePagePermission_EveryEndpointShouldReturn403()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(permissions: [MagicObjectHelper.角色_首頁]));
        var id = Guid.NewGuid();

        var requests = new (HttpMethod Method, string Url, object? Body)[]
        {
            (HttpMethod.Get, $"{Base}/info", null),
            (HttpMethod.Get, $"{Base}/records?workflow={ReportWorkflow}", null),
            (HttpMethod.Get, $"{Base}/records/{id}", null),
            (HttpMethod.Put, $"{Base}/records/{id}/title", new AgentWorkflowRenameRequestDto { Title = "x" }),
            (HttpMethod.Delete, $"{Base}/records/{id}", null),
            (HttpMethod.Post, $"{Base}/runs", new AgentWorkflowStartRequestDto { Workflow = ReportWorkflow, Input = "報告" }),
            (HttpMethod.Get, $"{Base}/runs/active", null),
            (HttpMethod.Post, $"{Base}/runs/{id}/responses", new AgentWorkflowRespondRequestDto { RequestId = "r" }),
            (HttpMethod.Post, $"{Base}/runs/{id}/stop", null),
            (HttpMethod.Post, $"{Base}/runs/{id}/abandon", null),
        };

        foreach (var (method, url, body) in requests)
        {
            using var request = new HttpRequestMessage(method, url);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, body.GetType());
            }

            var response = await client.SendAsync(request);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {url} → {(int)response.StatusCode}");
        }

        Assert.Empty(factory.Ai.Keys);
    }

    // ───────────────────────────── 完整流程：開始、接回、回覆、完成 ─────────────────────────────

    /// <summary>
    /// 蒐集 agent 提問 → 串流在提問停下來 → 「重新整理」後以 runs/active 接回 → 回答 → 審閱通過 → 核准收尾工具 → 完成。
    /// 工具以這個人的權限執行、稽核寫這個人；每一次模型呼叫記帳在這個人身上。
    /// </summary>
    [Fact]
    public async Task FullFlow_ShouldPauseForTheUser_ReconnectAfterRefresh_AndCompleteAsTheUser()
    {
        var user = await AddUserAsync(permissions:
        [
            MagicObjectHelper.角色_Agent工作流程,
            MagicObjectHelper.角色_專案項目,
            PermissionKey.For(MagicObjectHelper.角色_專案項目, PermissionActions.Edit),
        ]);
        var projectId = await AddProjectAsync("原本的描述");
        factory.Ai.Script(PromptTemplateKeys.ProjectReportGatherer,
            AiSse.ResponsesToolCall("call_q", "ask_user", """{"question":"是哪一個專案？"}"""),
            AiSse.Responses(["事實清單"]));
        factory.Ai.Script(PromptTemplateKeys.ProjectReportWriter, AiSse.Responses(["草稿一"]));
        factory.Ai.Script(PromptTemplateKeys.ProjectReportFinalizer,
            AiSse.ResponsesToolCall("call_a", "append_project_status_summary", $$"""{"projectId":{{projectId}},"summary":"進度正常"}"""),
            AiSse.Responses(["已附加摘要。"]));
        using var client = await SignedInClientAsync(user);

        var first = await StreamAsync(client, $"{Base}/runs", new AgentWorkflowStartRequestDto { Workflow = ReportWorkflow, Input = " 幫我寫專案報告 " });

        var start = first[0];
        Assert.Equal(AgentWorkflowFrameKinds.Start, start.Kind);
        var runId = start.RunId!.Value;
        Assert.Equal("gpt-4o", start.ModelName);
        Assert.Equal(("User", "幫我寫專案報告"), (start.Entries![0].Kind, start.Entries[0].Text));
        Assert.Contains(first, frame => frame is { Kind: AgentWorkflowFrameKinds.AgentStarted, Agent: "蒐集" });
        var question = first[^1];
        Assert.Equal(AgentWorkflowFrameKinds.Request, question.Kind);
        Assert.Equal((AgentWorkflowRequestKinds.Question, "蒐集", "是哪一個專案？"), (question.Request!.Kind, question.Request.Agent, question.Request.Question));
        Assert.Contains(question.Entries!, entry => entry is { Kind: AiAgentTranscriptKinds.Notice, Agent: "蒐集", Text: "提問：是哪一個專案？" });
        Assert.DoesNotContain(first, frame => frame.Kind == AgentWorkflowFrameKinds.Result);

        // 重新整理：新的頁面（同一個登入）接回停下來等回覆的那一次。
        using var refreshed = await SignedInClientAsync(user);
        var active = await SendAsync<AgentWorkflowRunDto?>(refreshed, HttpMethod.Get, $"{Base}/runs/active");
        Assert.NotNull(active);
        Assert.Equal((runId, ReportWorkflow, "gpt-4o"), (active.RunId, active.Workflow, active.ModelName));
        var pending = Assert.Single(active.PendingRequests);
        Assert.Equal(question.Request.RequestId, pending.RequestId);
        Assert.Equal(AllEntries(first), active.Transcript.Select(Describe).ToList());

        var second = await StreamAsync(refreshed, $"{Base}/runs/{runId}/responses", new AgentWorkflowRespondRequestDto { RequestId = pending.RequestId, Answer = " 團隊A專案 " });
        Assert.Contains(second[0].Entries!, entry => entry is { Kind: AiAgentTranscriptKinds.User, Agent: "蒐集", Text: "團隊A專案" });
        var review = second[^1].Request!;
        Assert.Equal((AgentWorkflowRequestKinds.Review, "草稿一", 3), (review.Kind, review.Draft, review.RejectionsLeft));

        var third = await StreamAsync(refreshed, $"{Base}/runs/{runId}/responses",
            new AgentWorkflowRespondRequestDto { RequestId = review.RequestId, Decision = AgentWorkflowReviewDecisions.Approve });
        var approval = third[^1].Request!;
        Assert.Equal(AgentWorkflowRequestKinds.ToolApproval, approval.Kind);
        var tool = Assert.Single(approval.Approvals);
        Assert.Equal(("append_project_status_summary", "附加專案狀態摘要"), (tool.ToolName, tool.DisplayName));
        Assert.Contains(tool.Arguments, argument => argument.Key == "projectId");

        var last = await StreamAsync(refreshed, $"{Base}/runs/{runId}/responses",
            new AgentWorkflowRespondRequestDto { RequestId = approval.RequestId, ApprovedRequestIds = [tool.RequestId] });

        var result = last[^1];
        Assert.Equal(AgentWorkflowFrameKinds.Result, result.Kind);
        Assert.Equal(AgentWorkflowOutcomes.Completed, result.Result!.Outcome);
        Assert.Equal(5, result.Result.ModelCalls);
        Assert.Contains(last[0].Entries!, entry => entry is { Kind: AiAgentTranscriptKinds.Notice, Agent: "收尾", Text: "你核准了：附加專案狀態摘要" });
        Assert.Contains(last, frame => frame is { Kind: AgentWorkflowFrameKinds.Tool, Tool.ToolName: "append_project_status_summary" });

        // 工具以這個人的權限與團隊範圍執行，稽核寫這個人。
        Assert.StartsWith("原本的描述\n\n【狀態摘要 ", await DescriptionAsync(projectId), StringComparison.Ordinal);
        var invoked = Assert.Single(await AuditsAsync(AuditActions.Ai.ToolInvoked, user.Id));
        Assert.Equal(("AiTool", "append_project_status_summary", true), (invoked.TargetType, invoked.TargetId, invoked.Success));
        Assert.StartsWith("agent=ReportFinalizer; tool=append_project_status_summary（附加專案狀態摘要）; permission=", invoked.Detail, StringComparison.Ordinal);

        // 每一次模型呼叫都記在這個人身上（同一個 ConversationId）。
        var calls = await WaitForCallLogsAsync(runId, 5);
        Assert.All(calls, call => Assert.Equal((user.Id, user.Account), (call.UserId, call.Account)));

        // 結束後不再登錄（scope 已釋放），紀錄留在「我的執行紀錄」。
        Assert.Null(await SendAsync<AgentWorkflowRunDto?>(refreshed, HttpMethod.Get, $"{Base}/runs/active"));
        Assert.Null(factory.Registry.Find(runId, user.Id));
        var detail = await SendAsync<AgentWorkflowRecordDetailDto>(refreshed, HttpMethod.Get, $"{Base}/records/{runId}");
        Assert.Equal(active.Transcript.Count + AllEntries(second).Count + AllEntries(third).Count + AllEntries(last).Count, detail.Transcript.Count);
        Assert.Equal(AiAgentTranscriptKinds.Assistant, detail.Transcript[^1].Kind);
        Assert.Equal("已附加摘要。", detail.Transcript[^1].Text);
    }

    [Fact]
    public async Task MultiReview_ShouldStreamEachReviewerAndTheOutput_InOneRequest()
    {
        factory.Ai.Script(PromptTemplateKeys.ReviewRequirements, AiSse.Responses(["意見-需求"]));
        factory.Ai.Script(PromptTemplateKeys.ReviewTechnical, AiSse.Responses(["意見-技術"]));
        factory.Ai.Script(PromptTemplateKeys.ReviewUsability, AiSse.Responses(["意見-體驗"]));
        factory.Ai.Script(PromptTemplateKeys.ReviewAggregator, AiSse.Responses(["彙整", "結果"]));
        using var client = await SignedInClientAsync(await AddUserAsync());

        var frames = await StreamAsync(client, $"{Base}/runs", new AgentWorkflowStartRequestDto { Workflow = "MultiPerspectiveReview", Input = "一份規格" });

        Assert.Equal(AgentWorkflowOutcomes.Completed, frames[^1].Result!.Outcome);
        // 三位審查者並行（順序不定），彙整最後才開始。
        var startedAgents = frames.Where(x => x.Kind == AgentWorkflowFrameKinds.AgentStarted).Select(x => x.Agent!).ToList();
        Assert.Equal(4, startedAgents.Count);
        Assert.Equal(
            new[] { "需求完整性", "技術可行性與風險", "使用者體驗" }.Order(StringComparer.Ordinal),
            startedAgents.Take(3).Order(StringComparer.Ordinal));
        Assert.Equal("彙整", startedAgents[^1]);
        Assert.Equal("彙整結果", string.Concat(frames.Where(x => x is { Kind: AgentWorkflowFrameKinds.Delta, Agent: "彙整" }).Select(x => x.Text)));
        var output = Assert.Single(frames, x => x.Kind == AgentWorkflowFrameKinds.Output).Output!;
        Assert.Equal(("彙整意見", "彙整結果"), (output.Title, output.Markdown));
        Assert.Contains(AllEntries(frames), entry => entry == "Assistant|使用者體驗|意見-體驗");
        Assert.Contains(AllEntries(frames), entry => entry == "Output|彙整意見|彙整結果");
    }

    // ───────────────────────────── 只有擁有者能操作 ─────────────────────────────

    [Fact]
    public async Task AnotherUser_ShouldNotSeeOrOperateTheRun()
    {
        var owner = await AddUserAsync();
        using var ownerClient = await SignedInClientAsync(owner);
        var (runId, pending) = await StartToQuestionAsync(ownerClient);
        using var other = await SignedInClientAsync(await AddUserAsync());

        Assert.Null(await SendAsync<AgentWorkflowRunDto?>(other, HttpMethod.Get, $"{Base}/runs/active"));
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"{Base}/runs/{runId}/responses",
            new AgentWorkflowRespondRequestDto { RequestId = pending.RequestId, Answer = "搶答" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"{Base}/runs/{runId}/stop", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"{Base}/runs/{runId}/abandon", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{Base}/records/{runId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"{Base}/records/{runId}")).StatusCode);

        // 擁有者的執行原封不動。
        var active = await SendAsync<AgentWorkflowRunDto?>(ownerClient, HttpMethod.Get, $"{Base}/runs/active");
        Assert.Equal(pending.RequestId, Assert.Single(active!.PendingRequests).RequestId);
    }

    [Fact]
    public async Task OneUser_ShouldHaveOnlyOneRunAtATime()
    {
        using var client = await SignedInClientAsync(await AddUserAsync());
        await StartToQuestionAsync(client);

        var response = await client.PostAsJsonAsync($"{Base}/runs", new AgentWorkflowStartRequestDto { Workflow = ReportWorkflow, Input = "再一次" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ───────────────────────────── 閒置逾時、停止、斷線、放棄 ─────────────────────────────

    [Fact]
    public async Task IdleTimeout_ShouldEndTheRunAsInterrupted_AndReleaseItsScope()
    {
        var user = await AddUserAsync();
        using var client = await SignedInClientAsync(user);
        var (runId, pending) = await StartToQuestionAsync(client);
        var entry = factory.Registry.Find(runId, user.Id)!;

        // 同一個測試主機裡其他測試留下的執行也會一起被清掉，所以只看這一次。
        await factory.Registry.SweepAsync(DateTime.UtcNow.AddMinutes(29));
        Assert.NotNull(factory.Registry.Find(runId, user.Id));

        Assert.True(await factory.Registry.SweepAsync(DateTime.UtcNow + AiWorkflowRunRegistry.IdleTimeout + TimeSpan.FromMinutes(1)) >= 1);

        Assert.Null(factory.Registry.Find(runId, user.Id));
        Assert.Throws<ObjectDisposedException>(() => entry.Services.GetRequiredService<CurrentUserService>());
        Assert.Equal(AiWorkflowOutcome.Interrupted, entry.Run.Result!.Outcome);
        Assert.Null(await SendAsync<AgentWorkflowRunDto?>(client, HttpMethod.Get, $"{Base}/runs/active"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"{Base}/runs/{runId}/responses",
            new AgentWorkflowRespondRequestDto { RequestId = pending.RequestId, Answer = "太晚了" })).StatusCode);
        var detail = await SendAsync<AgentWorkflowRecordDetailDto>(client, HttpMethod.Get, $"{Base}/records/{runId}");
        Assert.Equal("執行中斷（離開頁面），工作流程無法從中途接續。", detail.Transcript[^1].Text);
    }

    [Fact]
    public async Task Stop_ShouldCancelTheWholeWorkflow_AndEndTheStreamWithACanceledResult()
    {
        var user = await AddUserAsync();
        factory.Ai.Block(PromptTemplateKeys.ProjectReportGatherer);
        using var client = await SignedInClientAsync(user);
        using var response = await OpenStreamAsync(client, $"{Base}/runs", new AgentWorkflowStartRequestDto { Workflow = ReportWorkflow, Input = "報告" });
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        var runId = (await ReadFrameAsync(reader)).RunId!.Value;
        await factory.Ai.WaitUntilBlockedAsync();

        var stop = await client.PostAsync($"{Base}/runs/{runId}/stop", null);

        Assert.Equal(HttpStatusCode.OK, stop.StatusCode);
        var rest = await ReadAllFramesAsync(reader);
        Assert.Equal(AgentWorkflowOutcomes.Canceled, rest[^1].Result!.Outcome);
        Assert.Contains(AllEntries(rest), entry => entry == "Notice||已停止工作流程。");
        Assert.True(await factory.Ai.WaitUntilCanceledAsync());
        Assert.Null(factory.Registry.Find(runId, user.Id));
    }

    /// <summary>串流途中瀏覽器斷線（重新整理、關分頁、離開頁面）＝ Server 版的「離開頁面」：取消上游呼叫、存成「中斷」、釋放。</summary>
    [Fact]
    public async Task ClientDisconnectingMidStream_ShouldInterruptTheRun()
    {
        var user = await AddUserAsync();
        factory.Ai.Block(PromptTemplateKeys.ProjectReportGatherer);
        using var client = await SignedInClientAsync(user);
        using var cancel = new CancellationTokenSource();
        var response = await OpenStreamAsync(client, $"{Base}/runs", new AgentWorkflowStartRequestDto { Workflow = ReportWorkflow, Input = "報告" }, cancel.Token);
        var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        var runId = (await ReadFrameAsync(reader)).RunId!.Value;
        await factory.Ai.WaitUntilBlockedAsync();

        cancel.Cancel();
        reader.Dispose();
        response.Dispose();

        Assert.True(await factory.Ai.WaitUntilCanceledAsync());
        await WaitUntilAsync(() => factory.Registry.Find(runId, user.Id) is null);
        var detail = await SendAsync<AgentWorkflowRecordDetailDto>(client, HttpMethod.Get, $"{Base}/records/{runId}");
        Assert.Equal("執行中斷（離開頁面），工作流程無法從中途接續。", detail.Transcript[^1].Text);
    }

    [Fact]
    public async Task Abandon_ShouldEndTheWaitingRunAsInterrupted()
    {
        var user = await AddUserAsync();
        using var client = await SignedInClientAsync(user);
        var (runId, _) = await StartToQuestionAsync(client);

        var response = await client.PostAsync($"{Base}/runs/{runId}/abandon", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(factory.Registry.Find(runId, user.Id));
        var detail = await SendAsync<AgentWorkflowRecordDetailDto>(client, HttpMethod.Get, $"{Base}/records/{runId}");
        Assert.Equal("執行中斷（離開頁面），工作流程無法從中途接續。", detail.Transcript[^1].Text);

        // 放棄之後可以開始新的一次。
        factory.Ai.Script(PromptTemplateKeys.ProjectReportGatherer, AiSse.ResponsesToolCall("call_q2", "ask_user", """{"question":"還是哪個？"}"""));
        Assert.Equal(AgentWorkflowRequestKinds.Question,
            (await StreamAsync(client, $"{Base}/runs", new AgentWorkflowStartRequestDto { Workflow = ReportWorkflow, Input = "重來" }))[^1].Request!.Kind);
    }

    // ───────────────────────────── 規則檢查在伺服器 ─────────────────────────────

    [Fact]
    public async Task BlankAnswer_ShouldReturn400_AndKeepTheRequestWaiting()
    {
        using var client = await SignedInClientAsync(await AddUserAsync());
        var (runId, pending) = await StartToQuestionAsync(client);

        var result = await RawAsync(client, HttpMethod.Post, $"{Base}/runs/{runId}/responses", new AgentWorkflowRespondRequestDto { RequestId = pending.RequestId, Answer = "   " });

        Assert.Equal(400, result.StatusCode);
        Assert.Equal("請輸入回答。", result.UserMessage());
        var active = await SendAsync<AgentWorkflowRunDto?>(client, HttpMethod.Get, $"{Base}/runs/active");
        Assert.Equal(pending.RequestId, Assert.Single(active!.PendingRequests).RequestId);
        Assert.DoesNotContain(active.Transcript, entry => entry.Kind == AiAgentTranscriptKinds.User && entry.Agent == "蒐集");
    }

    [Fact]
    public async Task ReviewRules_ShouldBeCheckedOnTheServer()
    {
        factory.Ai.Script(PromptTemplateKeys.ProjectReportGatherer, AiSse.Responses(["事實"]));
        factory.Ai.Script(PromptTemplateKeys.ProjectReportWriter,
            AiSse.Responses(["v1"]), AiSse.Responses(["v2"]), AiSse.Responses(["v3"]), AiSse.Responses(["v4"]));
        using var client = await SignedInClientAsync(await AddUserAsync());
        var frames = await StreamAsync(client, $"{Base}/runs", new AgentWorkflowStartRequestDto { Workflow = ReportWorkflow, Input = "報告" });
        var runId = frames[0].RunId!.Value;
        var review = frames[^1].Request!;

        var noComment = await RawAsync(client, HttpMethod.Post, $"{Base}/runs/{runId}/responses",
            new AgentWorkflowRespondRequestDto { RequestId = review.RequestId, Decision = AgentWorkflowReviewDecisions.Reject, Comment = " " });
        var unknown = await RawAsync(client, HttpMethod.Post, $"{Base}/runs/{runId}/responses",
            new AgentWorkflowRespondRequestDto { RequestId = review.RequestId, Decision = "Maybe" });
        var tooLong = await RawAsync(client, HttpMethod.Post, $"{Base}/runs/{runId}/responses",
            new AgentWorkflowRespondRequestDto { RequestId = review.RequestId, Decision = AgentWorkflowReviewDecisions.Reject, Comment = new string('長', 2001) });

        Assert.Equal((400, "退回時請寫下意見。"), (noComment.StatusCode, noComment.UserMessage()));
        Assert.Equal(400, unknown.StatusCode);
        Assert.Equal(400, tooLong.StatusCode);

        for (var i = 0; i < ProjectStatusReportWorkflow.MaxRejections; i++)
        {
            review = (await StreamAsync(client, $"{Base}/runs/{runId}/responses",
                new AgentWorkflowRespondRequestDto { RequestId = review.RequestId, Decision = AgentWorkflowReviewDecisions.Reject, Comment = $"意見{i}" }))[^1].Request!;
        }

        Assert.Equal(("v4", 0), (review.Draft, review.RejectionsLeft));
        var noRejectionsLeft = await RawAsync(client, HttpMethod.Post, $"{Base}/runs/{runId}/responses",
            new AgentWorkflowRespondRequestDto { RequestId = review.RequestId, Decision = AgentWorkflowReviewDecisions.Reject, Comment = "再改" });
        Assert.Equal((400, "已經沒有退回次數，只能通過或結束。"), (noRejectionsLeft.StatusCode, noRejectionsLeft.UserMessage()));

        var ended = await StreamAsync(client, $"{Base}/runs/{runId}/responses",
            new AgentWorkflowRespondRequestDto { RequestId = review.RequestId, Decision = AgentWorkflowReviewDecisions.End });
        Assert.Equal(AgentWorkflowOutcomes.Ended, ended[^1].Result!.Outcome);
    }

    [Fact]
    public async Task AnsweredRequest_ShouldReturn409_WhenSentAgain()
    {
        using var client = await SignedInClientAsync(await AddUserAsync());
        var (runId, pending) = await StartToQuestionAsync(client);
        factory.Ai.Script(PromptTemplateKeys.ProjectReportGatherer, AiSse.ResponsesToolCall("call_q2", "ask_user", """{"question":"哪一季？"}"""));
        await StreamAsync(client, $"{Base}/runs/{runId}/responses", new AgentWorkflowRespondRequestDto { RequestId = pending.RequestId, Answer = "團隊A專案" });

        var again = await RawAsync(client, HttpMethod.Post, $"{Base}/runs/{runId}/responses", new AgentWorkflowRespondRequestDto { RequestId = pending.RequestId, Answer = "再答一次" });

        Assert.Equal(409, again.StatusCode);
    }

    [Fact]
    public async Task Start_WithAnUnknownWorkflowOrBlankInput_ShouldReturn400()
    {
        using var client = await SignedInClientAsync(await AddUserAsync());

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{Base}/runs", new AgentWorkflowStartRequestDto { Workflow = "Nope", Input = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{Base}/runs", new AgentWorkflowStartRequestDto { Workflow = ReportWorkflow, Input = "  " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{Base}/runs",
            new AgentWorkflowStartRequestDto { Workflow = ReportWorkflow, Input = new string('長', AgentWorkflowStartRequestDto.MaxInputLength + 1) })).StatusCode);
        Assert.Empty(factory.Ai.Keys);
    }

    // ───────────────────────────── 工具拒絕、沒有權限、紀錄 ─────────────────────────────

    [Fact]
    public async Task RejectingTheTool_ShouldAuditAiToolRejectedAsTheUser()
    {
        var user = await AddUserAsync(permissions:
        [
            MagicObjectHelper.角色_Agent工作流程,
            MagicObjectHelper.角色_專案項目,
            PermissionKey.For(MagicObjectHelper.角色_專案項目, PermissionActions.Edit),
        ]);
        var projectId = await AddProjectAsync("不要動");
        using var client = await SignedInClientAsync(user);
        var (runId, approval) = await RunToFinalizerAsync(client, projectId);

        var frames = await StreamAsync(client, $"{Base}/runs/{runId}/responses", new AgentWorkflowRespondRequestDto { RequestId = approval.RequestId });

        Assert.Equal(AgentWorkflowOutcomes.Completed, frames[^1].Result!.Outcome);
        Assert.Contains(frames[0].Entries!, entry => entry.Text == "你拒絕了：附加專案狀態摘要");
        var rejected = Assert.Single(await AuditsAsync(AuditActions.Ai.ToolRejected, user.Id));
        Assert.Equal(("AiTool", "append_project_status_summary"), (rejected.TargetType, rejected.TargetId));
        Assert.Empty(await AuditsAsync(AuditActions.Ai.ToolInvoked, user.Id));
        Assert.Equal("不要動", await DescriptionAsync(projectId));
    }

    /// <summary>執行 scope 裡的目前使用者沒有專案編輯權限：收尾工具直接拒絕、不請使用者核准，稽核寫這個人。</summary>
    [Fact]
    public async Task ToolWithoutPermission_ShouldBeDeniedWithoutAsking_AndAuditedAsTheUser()
    {
        var user = await AddUserAsync();
        var projectId = await AddProjectAsync("沒有權限");
        using var client = await SignedInClientAsync(user);
        factory.Ai.Script(PromptTemplateKeys.ProjectReportGatherer, AiSse.Responses(["事實"]));
        factory.Ai.Script(PromptTemplateKeys.ProjectReportWriter, AiSse.Responses(["報告"]));
        factory.Ai.Script(PromptTemplateKeys.ProjectReportFinalizer,
            AiSse.ResponsesToolCall("call_a", "append_project_status_summary", $$"""{"projectId":{{projectId}},"summary":"摘要"}"""),
            AiSse.Responses(["沒有權限，未附加。"]));
        var frames = await StreamAsync(client, $"{Base}/runs", new AgentWorkflowStartRequestDto { Workflow = ReportWorkflow, Input = "報告" });
        var runId = frames[0].RunId!.Value;

        var done = await StreamAsync(client, $"{Base}/runs/{runId}/responses",
            new AgentWorkflowRespondRequestDto { RequestId = frames[^1].Request!.RequestId, Decision = AgentWorkflowReviewDecisions.Approve });

        Assert.Equal(AgentWorkflowOutcomes.Completed, done[^1].Result!.Outcome);
        Assert.DoesNotContain(done, frame => frame.Kind == AgentWorkflowFrameKinds.Request);
        var denied = Assert.Single(await AuditsAsync(AuditActions.Ai.ToolDenied, user.Id));
        Assert.Equal("append_project_status_summary", denied.TargetId);
        Assert.Equal("沒有權限", await DescriptionAsync(projectId));
    }

    [Fact]
    public async Task Records_ShouldListOpenRenameAndDelete_AndAuditTheDelete()
    {
        var user = await AddUserAsync();
        using var client = await SignedInClientAsync(user);
        var (runId, _) = await StartToQuestionAsync(client);

        var list = await (await client.GetAsync($"{Base}/records?workflow={ReportWorkflow}")).Content.ReadAsStringAsync();
        var records = JsonSerializer.Deserialize<ApiResult<List<AgentWorkflowRecordDto>>>(list, Json)!.Data!;
        var record = Assert.Single(records);
        Assert.Equal((runId, "幫我寫專案報告"), (record.ConversationId, record.Title));
        Assert.False(Regex.IsMatch(Regex.Replace(list, "\"timestamp\":\"[^\"]*\"", string.Empty), @"T\d{2}:\d{2}:\d{2}(\.\d+)?([+-]\d{2}:\d{2}|Z)"), list);
        Assert.Empty(await SendAsync<List<AgentWorkflowRecordDto>>(client, HttpMethod.Get, $"{Base}/records?workflow=MultiPerspectiveReview"));
        Assert.Equal(400, (await RawAsync(client, HttpMethod.Get, $"{Base}/records?workflow=Nope")).StatusCode);

        await SendAsync<object>(client, HttpMethod.Put, $"{Base}/records/{runId}/title", new AgentWorkflowRenameRequestDto { Title = "新名字" });
        Assert.Equal("新名字", (await SendAsync<List<AgentWorkflowRecordDto>>(client, HttpMethod.Get, $"{Base}/records?workflow={ReportWorkflow}"))[0].Title);
        Assert.Equal(400, (await RawAsync(client, HttpMethod.Put, $"{Base}/records/{runId}/title", new AgentWorkflowRenameRequestDto { Title = " " })).StatusCode);
        Assert.Empty(await AuditsAsync("AiAgentConversation.Rename", user.Id));

        // 刪除還在等回覆的那一次：先放棄（存成中斷）再刪，同 Server 版。
        await SendAsync<object>(client, HttpMethod.Delete, $"{Base}/records/{runId}");

        Assert.Null(factory.Registry.Find(runId, user.Id));
        Assert.Empty(await SendAsync<List<AgentWorkflowRecordDto>>(client, HttpMethod.Get, $"{Base}/records?workflow={ReportWorkflow}"));
        Assert.Equal(404, (await RawAsync(client, HttpMethod.Get, $"{Base}/records/{runId}")).StatusCode);
        var audit = Assert.Single(await AuditsAsync(AuditActions.AiAgentConversation.Delete, user.Id));
        Assert.Equal(("AiAgentConversation", runId.ToString(), "agent=ProjectStatusReport"), (audit.TargetType, audit.TargetId, audit.Detail));
        Assert.Equal(404, (await RawAsync(client, HttpMethod.Delete, $"{Base}/records/{runId}")).StatusCode);
    }

    // ───────────────────────────── 輔助 ─────────────────────────────

    private async Task<(Guid RunId, AgentWorkflowPendingRequestDto Pending)> StartToQuestionAsync(HttpClient client)
    {
        factory.Ai.Script(PromptTemplateKeys.ProjectReportGatherer, AiSse.ResponsesToolCall("call_q", "ask_user", """{"question":"是哪一個專案？"}"""));
        var frames = await StreamAsync(client, $"{Base}/runs", new AgentWorkflowStartRequestDto { Workflow = ReportWorkflow, Input = "幫我寫專案報告" });
        return (frames[0].RunId!.Value, frames[^1].Request!);
    }

    private async Task<(Guid RunId, AgentWorkflowPendingRequestDto Approval)> RunToFinalizerAsync(HttpClient client, int projectId)
    {
        factory.Ai.Script(PromptTemplateKeys.ProjectReportGatherer, AiSse.Responses(["事實"]));
        factory.Ai.Script(PromptTemplateKeys.ProjectReportWriter, AiSse.Responses(["報告"]));
        factory.Ai.Script(PromptTemplateKeys.ProjectReportFinalizer,
            AiSse.ResponsesToolCall("call_a", "append_project_status_summary", $$"""{"projectId":{{projectId}},"summary":"摘要"}"""),
            AiSse.Responses(["使用者拒絕了。"]));
        var frames = await StreamAsync(client, $"{Base}/runs", new AgentWorkflowStartRequestDto { Workflow = ReportWorkflow, Input = "報告" });
        var runId = frames[0].RunId!.Value;
        var approval = (await StreamAsync(client, $"{Base}/runs/{runId}/responses",
            new AgentWorkflowRespondRequestDto { RequestId = frames[^1].Request!.RequestId, Decision = AgentWorkflowReviewDecisions.Approve }))[^1].Request!;
        Assert.Equal(AgentWorkflowRequestKinds.ToolApproval, approval.Kind);
        return (runId, approval);
    }

    private static List<string> AllEntries(IEnumerable<AgentWorkflowFrameDto> frames)
        => frames.SelectMany(frame => frame.Entries ?? []).Select(Describe).ToList();

    private static string Describe(AgentWorkflowEntryDto entry) => $"{entry.Kind}|{entry.Agent}|{entry.Text}";

    private static async Task<List<AgentWorkflowFrameDto>> StreamAsync(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} → {(int)response.StatusCode} {text}");
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<AgentWorkflowFrameDto>(line, Json)!)
            .Where(frame => frame.Kind != AgentWorkflowFrameKinds.Ping)
            .ToList();
    }

    private static async Task<HttpResponseMessage> OpenStreamAsync(HttpClient client, string url, object body, CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body, body.GetType()) };
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response;
    }

    private static async Task<AgentWorkflowFrameDto> ReadFrameAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)) is { } line)
        {
            var frame = JsonSerializer.Deserialize<AgentWorkflowFrameDto>(line, Json)!;
            if (frame.Kind != AgentWorkflowFrameKinds.Ping)
            {
                return frame;
            }
        }

        throw new Xunit.Sdk.XunitException("串流提早結束。");
    }

    private static async Task<List<AgentWorkflowFrameDto>> ReadAllFramesAsync(StreamReader reader)
    {
        var frames = new List<AgentWorkflowFrameDto>();
        while (await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)) is { } line)
        {
            var frame = JsonSerializer.Deserialize<AgentWorkflowFrameDto>(line, Json)!;
            if (frame.Kind != AgentWorkflowFrameKinds.Ping)
            {
                frames.Add(frame);
            }
        }

        return frames;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && condition() == false; i++)
        {
            await Task.Delay(100);
        }

        Assert.True(condition(), "等不到預期的狀態。");
    }

    private async Task<List<AiCallLog>> WaitForCallLogsAsync(Guid conversationId, int count)
    {
        for (var i = 0; i < 100; i++)
        {
            await using var context = await CreateContextAsync();
            var calls = await context.AiCallLog.Where(x => x.ConversationId == conversationId).ToListAsync();
            if (calls.Count >= count)
            {
                Assert.Equal(count, calls.Count);
                return calls;
            }

            await Task.Delay(100);
        }

        throw new Xunit.Sdk.XunitException($"等不到 {count} 筆 AI 對話紀錄。");
    }

    private async Task<List<AuditLog>> AuditsAsync(string action, int actorId)
    {
        await using var context = await CreateContextAsync();
        return await context.AuditLog.Where(x => x.Action == action && x.ActorUserId == actorId).ToListAsync();
    }

    private async Task<string?> DescriptionAsync(int projectId)
    {
        await using var context = await CreateContextAsync();
        return (await context.Project.AsNoTracking().SingleAsync(x => x.Id == projectId)).Description;
    }

    private async Task<int> AddProjectAsync(string description)
    {
        await using var context = await CreateContextAsync();
        var project = new Project
        {
            Title = Unique("工作流程專案"),
            Description = description,
            Status = "進行中",
            Priority = "中",
            Owner = "測試",
        };
        context.Project.Add(project);
        await context.SaveChangesAsync();
        return project.Id;
    }

    private static async Task<T> SendAsync<T>(HttpClient client, HttpMethod method, string url, object? body = null)
    {
        var result = await RawAsync<T>(client, method, url, body);
        Assert.True(result.Success, $"{method} {url} → {result.StatusCode} {result.UserMessage()}");
        return result.Data!;
    }

    private static Task<ApiResult<object>> RawAsync(HttpClient client, HttpMethod method, string url, object? body = null)
        => RawAsync<object>(client, method, url, body);

    private static async Task<ApiResult<T>> RawAsync<T>(HttpClient client, HttpMethod method, string url, object? body)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ApiResult<T>>(text, Json)!;
        Assert.Equal((int)response.StatusCode, result.StatusCode);
        return result;
    }

    private async Task<HttpClient> SignedInClientAsync(MyUser user)
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/Auth/login", new LoginRequestDto { Account = user.Account, Password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var tokens = (await login.Content.ReadFromJsonAsync<ApiResult<TokenResponseDto>>())!.Data!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    /// <summary>非管理員；預設只有「Agent 工作流程」頁面權限。</summary>
    private async Task<MyUser> AddUserAsync(List<string>? permissions = null)
    {
        permissions ??= [MagicObjectHelper.角色_Agent工作流程];
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        var writer = scope.ServiceProvider.GetRequiredService<IRbacWriteService>();
        var role = new RoleView { Name = Unique("角色"), TabViewJson = JsonSerializer.Serialize(permissions) };
        context.RoleView.Add(role);
        await context.SaveChangesAsync();
        await writer.SyncRolePermissionsAsync(role.Id, permissions);

        var account = Unique("m8wf-");
        var user = new MyUser
        {
            Account = account,
            Name = account,
            Password = SecurePasswordHasher.HashPassword(Password),
            Status = true,
            IsAdmin = false,
            RoleViewId = role.Id,
            PasswordChangedAtUtc = DateTime.UtcNow,
        };
        context.MyUser.Add(user);
        await context.SaveChangesAsync();
        await writer.SyncUserRolesAsync(user.Id, [role.Id]);
        return user;
    }

    private Task<BackendDBContext> CreateContextAsync()
        => factory.Services.GetRequiredService<IDbContextFactory<BackendDBContext>>().CreateDbContextAsync();

    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..Math.Min(prefix.Length + 12, prefix.Length + 32)];
}

/// <summary>整合測試主機：AI 設定完整（OpenAI），上游模型換成 <see cref="ScriptedWorkflowAi"/>，系統提示詞只放種類代碼。</summary>
public sealed class ApiTestApplicationFactoryWithScriptedWorkflowAi : ApiTestApplicationFactory
{
    public ScriptedWorkflowAi Ai { get; } = new();

    public AiWorkflowRunRegistry Registry => Services.GetRequiredService<AiWorkflowRunRegistry>();

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiSettings:Provider"] = "OpenAI",
                ["AiSettings:Endpoint"] = string.Empty,
                ["AiSettings:ApiKey"] = "integration-key",
                ["AiSettings:Model"] = "gpt-4o",
            });
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAiSystemPromptProvider>();
            services.AddSingleton<IAiSystemPromptProvider, KeyPromptProvider>();
            services.AddHttpClient(AiAgentFactory.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new ScriptedWorkflowAi.Handler(Ai));
        });
    }

    /// <summary>系統提示詞只放種類代碼，上游依它分給各 agent 的劇本。</summary>
    private sealed class KeyPromptProvider : IAiSystemPromptProvider
    {
        public Task<string> GetAsync(string templateKey, CancellationToken cancellationToken = default) => Task.FromResult($"AGENT:{templateKey}");

        public Task<string> GetInstructionsAsync(string templateKey, CancellationToken cancellationToken = default) => Task.FromResult($"AGENT:{templateKey}");
    }
}

/// <summary>
/// 假的上游模型：依請求裡的「AGENT:種類」分給各 agent 的劇本（SSE 回應依序取用）；
/// <see cref="Block"/> 的種類卡住直到請求被取消（測試停止與斷線）。
/// </summary>
public sealed class ScriptedWorkflowAi
{
    private static readonly Regex AgentKey = new("AGENT:(\\w+)", RegexOptions.Compiled);
    private readonly object gate = new();
    private readonly Dictionary<string, Queue<string>> scripts = [];
    private readonly HashSet<string> blocked = [];
    private TaskCompletionSource blockedReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>收到請求的種類（依序）。</summary>
    public List<string> Keys { get; } = [];

    public void Reset()
    {
        lock (gate)
        {
            scripts.Clear();
            blocked.Clear();
            Keys.Clear();
            blockedReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Script(string key, params string[] sse)
    {
        lock (gate)
        {
            if (scripts.TryGetValue(key, out var queue) == false)
            {
                queue = new Queue<string>();
                scripts[key] = queue;
            }

            foreach (var item in sse)
            {
                queue.Enqueue(item);
            }
        }
    }

    public void Block(string key)
    {
        lock (gate)
        {
            blocked.Add(key);
        }
    }

    public Task WaitUntilBlockedAsync() => blockedReached.Task.WaitAsync(TimeSpan.FromSeconds(30));

    public async Task<bool> WaitUntilCanceledAsync()
    {
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(30));
        return true;
    }

    private async Task<HttpResponseMessage> RespondAsync(string body, CancellationToken cancellationToken)
    {
        var key = AgentKey.Match(body).Groups[1].Value;
        bool block;
        string? sse = null;
        lock (gate)
        {
            Keys.Add(key);
            block = blocked.Contains(key);
            if (block == false && scripts.TryGetValue(key, out var queue) && queue.Count > 0)
            {
                sse = queue.Dequeue();
            }
        }

        if (block)
        {
            blockedReached.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                canceled.TrySetResult();
                throw;
            }
        }

        return sse is null
            ? throw new InvalidOperationException($"沒有 {key} 的劇本。")
            : AiSse.Response(sse);
    }

    public sealed class Handler(ScriptedWorkflowAi ai) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return await ai.RespondAsync(body, cancellationToken);
        }
    }
}
