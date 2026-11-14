using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
using MyProject.Dtos.Admins;
using MyProject.Dtos.Ai;
using MyProject.Dtos.Auths;
using MyProject.Dtos.Commons;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using MyProject.Web.Ai;
using MyProject.Web.Ai.Agents;
using MyProject.Web.Controllers;

namespace MyProject.Tests;

/// <summary>
/// Agent 對話的 Web API（0.10.7 起，WASM 遷移 M8；0.10.22 起 Agent 測試台移除，共用流程改經系統管理助理驗證）：
/// 每個請求接續一次對話、NDJSON 串流、伺服器端的規則（2,000 字、等待核准不能送、核准冪等、不可復原要輸入「確認」）、
/// 同一段對話的請求排隊、權限與稽核。
/// 上游模型換成可控的假回應（Responses API 的 SSE），系統管理助理的分派換成假的（回固定領域）；其餘（存檔、工具、稽核、記帳）都是真的。
/// </summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class AgentConversationApiTests : IClassFixture<ApiTestApplicationFactoryWithFakeAgentAi>
{
    private const string Password = "Passw0rd-1";
    private const string Assistant = "/api/v1/admin-assistant";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ApiTestApplicationFactoryWithFakeAgentAi factory;

    public AgentConversationApiTests(ApiTestApplicationFactoryWithFakeAgentAi factory)
    {
        this.factory = factory;
        factory.Upstream.Reset();
        factory.Router.Reset();
    }

    // ───────────────────────────── 送出訊息 ─────────────────────────────

    [Fact]
    public async Task Send_NewConversation_ShouldStreamStartEntriesTextAndResult_AndSaveIt()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        factory.Upstream.Enqueue(AiSse.Responses(["你好", "，我是小幫手。"]));

        var frames = await StreamAsync(client, $"{Assistant}/messages", new AgentMessageRequestDto { Message = "  嗨  " });

        var start = frames[0];
        Assert.Equal(AgentStreamFrameKinds.Start, start.Kind);
        Assert.NotNull(start.ConversationId);
        Assert.False(string.IsNullOrEmpty(start.ModelName));
        Assert.Equal(
            [(AiAgentTranscriptKinds.User, "嗨"), (AiAgentTranscriptKinds.Assistant, "你好，我是小幫手。")],
            frames.Where(frame => frame.Kind == AgentStreamFrameKinds.Entry).Select(frame => (frame.Entry!.Kind, frame.Entry.Text)));
        Assert.Equal("你好，我是小幫手。", string.Concat(frames.Where(frame => frame.Kind == AgentStreamFrameKinds.Delta).Select(frame => frame.Text)));
        var result = frames[^1];
        Assert.Equal(AgentStreamFrameKinds.Result, result.Kind);
        Assert.True(result.Result!.Success);

        // 使用者那一則要比任何文字先到（畫面才能先顯示自己的訊息）。
        Assert.True(frames.FindIndex(frame => frame.Kind == AgentStreamFrameKinds.Entry) < frames.FindIndex(frame => frame.Kind == AgentStreamFrameKinds.Delta));

        var list = await SendAsync<List<AgentConversationSummaryDto>>(client, HttpMethod.Get, $"{Assistant}/conversations");
        var item = Assert.Single(list, x => x.ConversationId == start.ConversationId);
        Assert.Equal("嗨", item.Title);
        Assert.False(item.HasPendingApproval);
    }

    [Fact]
    public async Task Send_ShouldContinueTheSavedConversation()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        factory.Upstream.Enqueue(AiSse.Responses(["第一則回覆"]));
        var id = (await StreamAsync(client, $"{Assistant}/messages", new AgentMessageRequestDto { Message = "第一則" }))[0].ConversationId!.Value;
        factory.Upstream.Enqueue(AiSse.Responses(["第二則回覆"]));

        var frames = await StreamAsync(client, $"{Assistant}/messages", new AgentMessageRequestDto { ConversationId = id, Message = "第二則" });

        Assert.Equal(id, frames[0].ConversationId);
        Assert.True(frames[^1].Result!.Success);
        // 接續：第二次呼叫帶著前文（第一則的回覆）。
        Assert.Contains("第一則回覆", Regex.Unescape(factory.Upstream.Bodies[1]), StringComparison.Ordinal);
        var opened = await SendAsync<AgentConversationDto>(client, HttpMethod.Get, $"{Assistant}/conversations/{id}");
        Assert.Equal(["第一則", "第一則回覆", "第二則", "第二則回覆"], opened.Transcript.Select(entry => entry.Text));
    }

    [Theory]
    [InlineData(AgentConversationLimits.MaxMessageLength + 1, HttpStatusCode.BadRequest)]
    [InlineData(0, HttpStatusCode.BadRequest)]
    public async Task Send_TooLongOrEmpty_ShouldBeRejectedBeforeCallingTheModel(int length, HttpStatusCode expected)
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));

        var response = await client.PostAsJsonAsync($"{Assistant}/messages", new AgentMessageRequestDto { Message = new string('字', length) });

        Assert.Equal(expected, response.StatusCode);
        Assert.Empty(factory.Upstream.Bodies);
    }

    [Fact]
    public async Task Send_ExactlyTheLimit_ShouldBeAccepted()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        factory.Upstream.Enqueue(AiSse.Responses(["收到"]));

        var frames = await StreamAsync(client, $"{Assistant}/messages", new AgentMessageRequestDto { Message = new string('字', AgentConversationLimits.MaxMessageLength) });

        Assert.True(frames[^1].Result!.Success);
    }

    [Fact]
    public async Task Send_ToSomeoneElsesConversation_ShouldReturn404()
    {
        using var owner = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        factory.Upstream.Enqueue(AiSse.Responses(["回覆"]));
        var id = (await StreamAsync(owner, $"{Assistant}/messages", new AgentMessageRequestDto { Message = "我的" }))[0].ConversationId!.Value;
        using var other = await SignedInClientAsync(await AddUserAsync(isAdmin: true));

        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"{Assistant}/messages", new AgentMessageRequestDto { ConversationId = id, Message = "偷看" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{Assistant}/conversations/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"{Assistant}/conversations/{id}")).StatusCode);
    }

    /// <summary>使用者中斷連線：這次執行被取消，已有的內容（使用者訊息與「已停止產生」）照樣存檔。</summary>
    [Fact]
    public async Task Send_WhenTheClientDisconnects_ShouldCancelTheRun_AndStillSaveIt()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        factory.Upstream.EnqueueUntilCanceled();

        Guid id;
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{Assistant}/messages") { Content = JsonContent.Create(new AgentMessageRequestDto { Message = "會被中斷" }) };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));
            id = Frame((await reader.ReadLineAsync(cts.Token))!).ConversationId!.Value;
            await factory.Upstream.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await cts.CancelAsync();
        }

        Assert.True(await factory.Upstream.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        var opened = await WaitForAsync(async () =>
        {
            var response = await client.GetAsync($"{Assistant}/conversations/{id}");
            return response.StatusCode == HttpStatusCode.OK ? (await response.Content.ReadFromJsonAsync<ApiResult<AgentConversationDto>>(Json))!.Data : null;
        });
        Assert.Equal(
            [(AiAgentTranscriptKinds.User, "會被中斷"), (AiAgentTranscriptKinds.Notice, "已停止產生，尚未收到任何內容。")],
            opened.Transcript.Select(entry => (entry.Kind, entry.Text)));
        Assert.Equal(0, factory.Services.GetRequiredService<AiAgentConversationLocks>().Count);
    }

    // ───────────────────────────── 核准 ─────────────────────────────

    [Fact]
    public async Task Approval_ShouldStopForTheUser_SurviveARefresh_AndRunOnceWhenApproved()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var (id, requestId) = await AskForTestNotificationAsync(client);

        // 重新整理：清單標「待核准」、打開對話時待核准還在（同一個編號）。
        var list = await SendAsync<List<AgentConversationSummaryDto>>(client, HttpMethod.Get, $"{Assistant}/conversations");
        Assert.True(Assert.Single(list, x => x.ConversationId == id).HasPendingApproval);
        var opened = await SendAsync<AgentConversationDto>(client, HttpMethod.Get, $"{Assistant}/conversations/{id}");
        var pending = Assert.Single(opened.PendingApprovals);
        Assert.Equal(requestId, pending.RequestId);
        Assert.Equal("發送測試通知", pending.DisplayName);

        // 等待核准時不能送新訊息。
        var blocked = await client.PostAsJsonAsync($"{Assistant}/messages", new AgentMessageRequestDto { ConversationId = id, Message = "再問一次" });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains(AgentConversationEndpoint.AwaitingApprovalMessage, await blocked.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        factory.Upstream.Enqueue(AiSse.Responses(["已發送。"]));
        var frames = await StreamAsync(client, $"{Assistant}/conversations/{id}/approvals", new AgentApprovalResponseRequestDto { ApprovedRequestIds = [requestId] });

        Assert.True(frames[^1].Result!.Success);
        Assert.Contains(frames, frame => frame.Entry is { Kind: AiAgentTranscriptKinds.Notice, Text: "你核准了：發送測試通知" });
        var invoked = Assert.Single(await AuditsAsync(AuditActions.Ai.ToolInvoked, admin.Id));
        Assert.Equal("AiTool", invoked.TargetType);
        Assert.Equal("send_test_notification", invoked.TargetId);

        // 同一個核准再送一次：已經處理過，409，工具不會再執行、也不會再呼叫模型。
        var calls = factory.Upstream.Bodies.Count;
        var again = await client.PostAsJsonAsync($"{Assistant}/conversations/{id}/approvals", new AgentApprovalResponseRequestDto { ApprovedRequestIds = [requestId] });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Single(await AuditsAsync(AuditActions.Ai.ToolInvoked, admin.Id));
        Assert.Equal(calls, factory.Upstream.Bodies.Count);
    }

    [Fact]
    public async Task Approval_Rejected_ShouldNotRunTheTool_AndAuditTheRejection()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var (id, requestId) = await AskForTestNotificationAsync(client);
        factory.Upstream.Enqueue(AiSse.Responses(["好的，沒有發送。"]));

        var frames = await StreamAsync(client, $"{Assistant}/conversations/{id}/approvals", new AgentApprovalResponseRequestDto { RejectedRequestIds = [requestId] });

        Assert.True(frames[^1].Result!.Success);
        Assert.Contains(frames, frame => frame.Entry is { Kind: AiAgentTranscriptKinds.Notice, Text: "你拒絕了：發送測試通知" });
        Assert.Empty(await AuditsAsync(AuditActions.Ai.ToolInvoked, admin.Id));
        var rejected = Assert.Single(await AuditsAsync(AuditActions.Ai.ToolRejected, admin.Id));
        Assert.False(rejected.Success);
        Assert.Equal("send_test_notification", rejected.TargetId);
        Assert.StartsWith("agent=AdminAssistant; tool=send_test_notification", rejected.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approval_WithNothingPending_OrUnknownIds_ShouldReturn409_WithoutCallingTheModel()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        factory.Upstream.Enqueue(AiSse.Responses(["回覆"]));
        var plain = (await StreamAsync(client, $"{Assistant}/messages", new AgentMessageRequestDto { Message = "嗨" }))[0].ConversationId!.Value;
        var (id, _) = await AskForTestNotificationAsync(client);
        var calls = factory.Upstream.Bodies.Count;

        var nothing = await client.PostAsJsonAsync($"{Assistant}/conversations/{plain}/approvals", new AgentApprovalResponseRequestDto { ApprovedRequestIds = ["x"] });
        var unknown = await client.PostAsJsonAsync($"{Assistant}/conversations/{id}/approvals", new AgentApprovalResponseRequestDto { ApprovedRequestIds = ["not-pending"] });
        var empty = await client.PostAsJsonAsync($"{Assistant}/conversations/{id}/approvals", new AgentApprovalResponseRequestDto());

        Assert.Equal(HttpStatusCode.Conflict, nothing.StatusCode);
        Assert.Contains(AgentConversationEndpoint.NothingPendingMessage, await nothing.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(calls, factory.Upstream.Bodies.Count);
        Assert.Single((await SendAsync<AgentConversationDto>(client, HttpMethod.Get, $"{Assistant}/conversations/{id}")).PendingApprovals);
    }

    /// <summary>兩個分頁同時按「核准」：後到的排隊，輪到時已沒有待核准（結果那一行 Conflict），工具只執行一次。</summary>
    [Fact]
    public async Task Approval_SentTwiceAtTheSameTime_ShouldRunTheToolOnce()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var (id, requestId) = await AskForTestNotificationAsync(client);
        var gate = factory.Upstream.EnqueueGated(AiSse.Responses(["已發送。"]));
        var body = new AgentApprovalResponseRequestDto { ApprovedRequestIds = [requestId] };

        var first = OpenStreamAsync(client, $"{Assistant}/conversations/{id}/approvals", body);
        await factory.Upstream.WaitForCallsAsync(2);
        var second = await OpenStreamAsync(client, $"{Assistant}/conversations/{id}/approvals", body).WaitAsync(TimeSpan.FromSeconds(10));
        gate.SetResult();
        var firstFrames = await ReadFramesAsync(await first);
        var secondText = await second.Content.ReadAsStringAsync();

        Assert.True(firstFrames[^1].Result!.Success);
        if (second.StatusCode == HttpStatusCode.OK)
        {
            var result = Frames(secondText)[^1].Result!;
            Assert.False(result.Success);
            Assert.Equal("Conflict", result.Reason);
        }
        else
        {
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        }

        Assert.Single(await AuditsAsync(AuditActions.Ai.ToolInvoked, admin.Id));
        Assert.Equal(2, factory.Upstream.Bodies.Count);
    }

    // ───────────────────────────── 併發 ─────────────────────────────

    /// <summary>
    /// 兩個分頁同時對同一段對話送出：後到的請求排隊（先回 200 串流、不呼叫模型），等前一個做完再接續，
    /// 讀得到前一個的內容；兩則都留在紀錄裡，誰也沒蓋掉誰。
    /// </summary>
    [Fact]
    public async Task TwoRequestsOnTheSameConversation_ShouldBeQueued_NotRejected()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        factory.Upstream.Enqueue(AiSse.Responses(["開場"]));
        var id = (await StreamAsync(client, $"{Assistant}/messages", new AgentMessageRequestDto { Message = "開始" }))[0].ConversationId!.Value;
        var gate = factory.Upstream.EnqueueGated(AiSse.Responses(["A 的回覆"]));
        factory.Upstream.Enqueue(AiSse.Responses(["B 的回覆"]));

        var a = OpenStreamAsync(client, $"{Assistant}/messages", new AgentMessageRequestDto { ConversationId = id, Message = "分頁 A" });
        await factory.Upstream.WaitForCallsAsync(2);
        var b = OpenStreamAsync(client, $"{Assistant}/messages", new AgentMessageRequestDto { ConversationId = id, Message = "分頁 B" });
        var bResponse = await b.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.OK, bResponse.StatusCode);
        await Task.Delay(300);
        Assert.Equal(2, factory.Upstream.Bodies.Count);

        // 執行中打開這段對話（例如第三個分頁）：等不到就讀上次存下的內容，不會卡到執行結束。
        var whileRunning = await SendAsync<AgentConversationDto>(client, HttpMethod.Get, $"{Assistant}/conversations/{id}");
        Assert.Equal(["開始", "開場"], whileRunning.Transcript.Select(entry => entry.Text));

        gate.SetResult();
        var aFrames = await ReadFramesAsync(await a);
        var bFrames = await ReadFramesAsync(bResponse);

        Assert.True(aFrames[^1].Result!.Success);
        Assert.True(bFrames[^1].Result!.Success);
        Assert.Contains("A 的回覆", Regex.Unescape(factory.Upstream.Bodies[2]), StringComparison.Ordinal);
        var opened = await SendAsync<AgentConversationDto>(client, HttpMethod.Get, $"{Assistant}/conversations/{id}");
        Assert.Equal(["開始", "開場", "分頁 A", "A 的回覆", "分頁 B", "B 的回覆"], opened.Transcript.Select(entry => entry.Text));
        Assert.Equal(0, factory.Services.GetRequiredService<AiAgentConversationLocks>().Count);
    }

    // ───────────────────────────── 清單、改名、刪除 ─────────────────────────────

    [Fact]
    public async Task RenameAndDelete_ShouldOnlyAuditTheDelete_AndTimesHaveNoOffset()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        factory.Upstream.Enqueue(AiSse.Responses(["回覆"]));
        var id = (await StreamAsync(client, $"{Assistant}/messages", new AgentMessageRequestDto { Message = "原本的標題" }))[0].ConversationId!.Value;

        var listText = await (await client.GetAsync($"{Assistant}/conversations")).Content.ReadAsStringAsync();
        AssertNoOffset(listText);
        Assert.Contains("updatedAt", listText, StringComparison.Ordinal);

        await SendAsync<object>(client, HttpMethod.Put, $"{Assistant}/conversations/{id}/title", new AgentRenameRequestDto { Title = "新標題" });
        Assert.Equal("新標題", Assert.Single(await SendAsync<List<AgentConversationSummaryDto>>(client, HttpMethod.Get, $"{Assistant}/conversations"), x => x.ConversationId == id).Title);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{Assistant}/conversations/{id}/title", new AgentRenameRequestDto { Title = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"{Assistant}/conversations/{Guid.NewGuid()}/title", new AgentRenameRequestDto { Title = "x" })).StatusCode);

        await SendAsync<object>(client, HttpMethod.Delete, $"{Assistant}/conversations/{id}");

        Assert.DoesNotContain(await SendAsync<List<AgentConversationSummaryDto>>(client, HttpMethod.Get, $"{Assistant}/conversations"), x => x.ConversationId == id);
        var audit = Assert.Single(await AuditsAsync(AuditActions.AiAgentConversation.Delete, admin.Id));
        Assert.Equal(id.ToString(), audit.TargetId);
        Assert.Equal("agent=AdminAssistant", audit.Detail);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{Assistant}/conversations/{id}")).StatusCode);
    }

    // ───────────────────────────── 權限 ─────────────────────────────

    [Fact]
    public async Task AdminAssistant_ForANonAdmin_ShouldReturn403()
    {
        var user = await AddUserAsync(isAdmin: false);
        using var client = await SignedInClientAsync(user);

        var responses = new[]
        {
            await client.GetAsync($"{Assistant}/info"),
            await client.GetAsync($"{Assistant}/conversations"),
            await client.GetAsync($"{Assistant}/conversations/{Guid.NewGuid()}"),
            await client.PostAsJsonAsync($"{Assistant}/messages", new AgentMessageRequestDto { Message = "嗨" }),
            await client.PostAsJsonAsync($"{Assistant}/conversations/{Guid.NewGuid()}/approvals", new AgentApprovalResponseRequestDto { ApprovedRequestIds = ["x"] }),
            await client.DeleteAsync($"{Assistant}/conversations/{Guid.NewGuid()}"),
            await client.GetAsync($"{Assistant}/patrol-reports"),
            await client.GetAsync($"{Assistant}/patrol-reports/{Guid.NewGuid()}"),
        };

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode));
        Assert.Empty(factory.Upstream.Bodies);
        var denied = await AuditsAsync(AuditActions.Permission.Denied, user.Id);
        Assert.Equal(responses.Length, denied.Count);
        Assert.All(denied, audit => Assert.Equal("需要管理員", audit.Detail));
    }

    // ───────────────────────────── 系統管理助理 ─────────────────────────────

    /// <summary>
    /// 不可復原的動作：沒有輸入「確認」回 400、什麼都沒做、待核准還在；輸入後才執行一次。
    /// 核准後沿用分派到的領域（監控）：送給模型的工具只有監控的異動工具，不是開對話那一輪的全部工具。
    /// </summary>
    [Fact]
    public async Task AdminAssistant_IrreversibleApproval_NeedsTheConfirmText_AndContinuesWithTheDispatchedDomain()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        factory.Router.Codes.Enqueue("monitoring");
        factory.Upstream.Enqueue(AiSse.ResponsesToolCall("call_x", "delete_exceptions", """{"ids":[987654]}"""));

        var frames = await StreamAsync(client, $"{Assistant}/messages", new AgentMessageRequestDto { Message = "刪掉例外 987654" });
        var id = frames[0].ConversationId!.Value;
        Assert.Contains(frames, frame => frame.Kind == AgentStreamFrameKinds.Label && frame.Label == "監控與診斷");
        var approval = Assert.Single(Assert.Single(frames, frame => frame.Kind == AgentStreamFrameKinds.Approvals).Approvals!);
        Assert.True(approval.IsIrreversible);
        Assert.Equal("監控與診斷", approval.Label);
        Assert.DoesNotContain("set_users_enabled", factory.Upstream.Bodies[0], StringComparison.Ordinal);

        var missing = await client.PostAsJsonAsync($"{Assistant}/conversations/{id}/approvals", new AgentApprovalResponseRequestDto { ApprovedRequestIds = [approval.RequestId] });
        var wrong = await client.PostAsJsonAsync($"{Assistant}/conversations/{id}/approvals", new AgentApprovalResponseRequestDto { ApprovedRequestIds = [approval.RequestId], ConfirmText = "確定" });

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains(AgentConversationEndpoint.ConfirmRequiredMessage, await missing.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Empty(await AuditsAsync(AuditActions.Ai.ToolInvoked, admin.Id));
        Assert.Single((await SendAsync<AgentConversationDto>(client, HttpMethod.Get, $"{Assistant}/conversations/{id}")).PendingApprovals);
        Assert.Single(factory.Upstream.Bodies);

        factory.Upstream.Enqueue(AiSse.Responses(["已刪除。"]));
        var approved = await StreamAsync(client, $"{Assistant}/conversations/{id}/approvals",
            new AgentApprovalResponseRequestDto { ApprovedRequestIds = [approval.RequestId], ConfirmText = " 確認 " });

        Assert.True(approved[^1].Result!.Success);
        var invoked = Assert.Single(await AuditsAsync(AuditActions.Ai.ToolInvoked, admin.Id));
        Assert.Equal("delete_exceptions", invoked.TargetId);
        Assert.StartsWith("agent=AdminAssistant; tool=delete_exceptions", invoked.Detail, StringComparison.Ordinal);

        // 核准後那一輪：仍是監控的工具（有 delete_exceptions、沒有帳號的 set_users_enabled），分派只呼叫過一次。
        var continued = factory.Upstream.Bodies[1];
        Assert.Contains("delete_exceptions", continued, StringComparison.Ordinal);
        Assert.DoesNotContain("set_users_enabled", continued, StringComparison.Ordinal);
        Assert.Equal(1, factory.Router.Calls);
        var answered = (await SendAsync<AgentConversationDto>(client, HttpMethod.Get, $"{Assistant}/conversations/{id}")).Transcript.Last();
        Assert.Equal((AiAgentTranscriptKinds.Assistant, "已刪除。", "監控與診斷"), (answered.Kind, answered.Text, answered.Agent));
    }

    [Fact]
    public async Task AdminAssistant_ReversibleApproval_DoesNotNeedTheConfirmText()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        factory.Router.Codes.Enqueue("monitoring");
        factory.Upstream.Enqueue(AiSse.ResponsesToolCall("call_n", "send_test_notification", "{}"));
        var frames = await StreamAsync(client, $"{Assistant}/messages", new AgentMessageRequestDto { Message = "發一則測試通知" });
        var approval = Assert.Single(Assert.Single(frames, frame => frame.Kind == AgentStreamFrameKinds.Approvals).Approvals!);
        Assert.False(approval.IsIrreversible);
        factory.Upstream.Enqueue(AiSse.Responses(["已發送。"]));

        var approved = await StreamAsync(client, $"{Assistant}/conversations/{frames[0].ConversationId}/approvals",
            new AgentApprovalResponseRequestDto { ApprovedRequestIds = [approval.RequestId] });

        Assert.True(approved[^1].Result!.Success);
        Assert.Equal("send_test_notification", Assert.Single(await AuditsAsync(AuditActions.Ai.ToolInvoked, admin.Id)).TargetId);
    }

    [Fact]
    public async Task AdminAssistant_Info_ShouldNotOfferReasoning_AndListTheRetention()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));

        var info = await SendAsync<AgentPageInfoDto>(client, HttpMethod.Get, $"{Assistant}/info");

        Assert.Null(info.UnavailableReason);
        Assert.Empty(info.ReasoningEffortValues);
        Assert.True(info.RetentionDays > 0);
    }

    /// <summary>巡檢報告與通知連結（/admin-assistant?patrolReport={id}）：清單、單份（Markdown 由畫面消毒）、找不到 404；時間不帶時差、不寫稽核。</summary>
    [Fact]
    public async Task PatrolReports_ListAndDeepLink()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var id = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var saved = await scope.ServiceProvider.GetRequiredService<AdminPatrolReportService>().SaveAsync(new AdminPatrolReportDraft(
                id, new DateTime(2026, 11, 8, 7, 30, 0), AdminPatrolStatuses.Abnormal, "排程與備份", JobRunTriggers.Manual, "support", 1, "gpt-test", false,
                "## 總結\n**備份失敗**"));
            Assert.NotNull(saved);
        }

        var listText = await (await client.GetAsync($"{Assistant}/patrol-reports")).Content.ReadAsStringAsync();
        var list = JsonSerializer.Deserialize<ApiResult<AdminPatrolReportListDto>>(listText, Json)!.Data!;
        var detail = await SendAsync<AdminPatrolReportDetailDto>(client, HttpMethod.Get, $"{Assistant}/patrol-reports/{id}");
        var missing = await client.GetAsync($"{Assistant}/patrol-reports/{Guid.NewGuid()}");

        var item = Assert.Single(list.Items, x => x.ReportId == id);
        Assert.Equal(new DateTime(2026, 11, 8, 7, 30, 0), item.CreatedAt);
        Assert.Equal(DateTimeKind.Unspecified, item.CreatedAt.Kind);
        Assert.Equal(("異常", "排程與備份", "Manual", "support"), (item.Status, item.AttentionSections, item.Trigger, item.TriggeredByAccount));
        Assert.True(list.RetentionDays > 0);
        AssertNoOffset(listText);
        Assert.Equal("## 總結\n**備份失敗**", detail.Markdown);
        Assert.Equal(id, detail.Summary.ReportId);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Contains(AdminAssistantController.PatrolReportNotFoundMessage, await missing.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Empty(await AuditsAsync(null, admin.Id, prefix: "AdminPatrol"));
    }

    // ───────────────────────────── 工具 ─────────────────────────────

    /// <summary>請模型發一則測試通知（可復原的異動工具，分派到監控），停在核准。</summary>
    private async Task<(Guid ConversationId, string RequestId)> AskForTestNotificationAsync(HttpClient client)
    {
        factory.Router.Codes.Enqueue("monitoring");
        factory.Upstream.Enqueue(AiSse.ResponsesToolCall("call_notify", "send_test_notification", "{}"));
        var frames = await StreamAsync(client, $"{Assistant}/messages", new AgentMessageRequestDto { Message = "發一則測試通知" });
        Assert.DoesNotContain(frames, frame => frame.Kind == AgentStreamFrameKinds.Result);
        Assert.Equal(AgentStreamFrameKinds.Approvals, frames[^1].Kind);
        return (frames[0].ConversationId!.Value, Assert.Single(frames[^1].Approvals!).RequestId);
    }

    private static void AssertNoOffset(string text)
    {
        var zoned = new Regex(@"T\d{2}:\d{2}:\d{2}(\.\d+)?([+-]\d{2}:\d{2}|Z)");
        var envelope = new Regex("\"timestamp\":\"[^\"]*\"");
        Assert.False(zoned.IsMatch(envelope.Replace(text, string.Empty)), text);
    }

    private static AgentStreamFrameDto Frame(string line) => JsonSerializer.Deserialize<AgentStreamFrameDto>(line, Json)!;

    private static List<AgentStreamFrameDto> Frames(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(Frame).Where(frame => frame.Kind != AgentStreamFrameKinds.Ping).ToList();

    private static async Task<List<AgentStreamFrameDto>> StreamAsync(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} → {(int)response.StatusCode} {text}");
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
        return Frames(text);
    }

    /// <summary>送出串流請求，標頭一到就回來（內容之後再讀）。</summary>
    private static Task<HttpResponseMessage> OpenStreamAsync(HttpClient client, string url, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body, body.GetType()) };
        return client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }

    private static async Task<List<AgentStreamFrameDto>> ReadFramesAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode} {text}");
            return Frames(text);
        }
    }

    private static async Task<T> WaitForAsync<T>(Func<Task<T?>> probe)
        where T : class
    {
        for (var i = 0; i < 100; i++)
        {
            if (await probe() is { } value)
            {
                return value;
            }

            await Task.Delay(100);
        }

        throw new Xunit.Sdk.XunitException("等不到預期的狀態。");
    }

    private async Task<List<AuditLog>> AuditsAsync(string? action, int actorId, string? prefix = null)
    {
        await using var context = await factory.Services.GetRequiredService<IDbContextFactory<BackendDBContext>>().CreateDbContextAsync();
        return await context.AuditLog
            .Where(x => x.ActorUserId == actorId && (action == null || x.Action == action) && (prefix == null || x.Action.StartsWith(prefix)))
            .ToListAsync();
    }

    private static async Task<T> SendAsync<T>(HttpClient client, HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{method} {url} → {(int)response.StatusCode} {text}");
        return JsonSerializer.Deserialize<ApiResult<T>>(text, Json)!.Data!;
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

    private async Task<MyUser> AddUserAsync(bool isAdmin)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        var writer = scope.ServiceProvider.GetRequiredService<IRbacWriteService>();
        var role = await context.RoleView.FirstAsync(x => x.Name == MagicObjectHelper.預設角色);
        var account = $"m8a-{Guid.NewGuid():N}"[..20];
        var user = new MyUser
        {
            Account = account,
            Name = account,
            Password = SecurePasswordHasher.HashPassword(Password),
            Status = true,
            IsAdmin = isAdmin,
            RoleViewId = role.Id,
            PasswordChangedAtUtc = DateTime.UtcNow,
        };
        context.MyUser.Add(user);
        await context.SaveChangesAsync();
        await writer.SyncUserRolesAsync(user.Id, [role.Id]);
        return user;
    }
}

/// <summary>整合測試主機：AI 設定完整（不會真的連線），上游模型換成 <see cref="ScriptedAgentUpstream"/>，系統管理助理的分派換成 <see cref="FakeRouterClient"/>。</summary>
public sealed class ApiTestApplicationFactoryWithFakeAgentAi : ApiTestApplicationFactory
{
    public ScriptedAgentUpstream Upstream { get; } = new();

    public FakeRouterClient Router { get; } = new();

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiSettings:Provider"] = "OpenAI",
                ["AiSettings:ApiKey"] = "integration-test-key",
                ["AiSettings:Model"] = "gpt-4o",
            });
        });
        builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient(AiAgentFactory.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new ForwardingHandler(Upstream));
            services.RemoveAll<IAiChatCompletionClient>();
            services.AddSingleton<IAiChatCompletionClient>(Router);
        });
    }

    /// <summary>HttpClientFactory 會丟棄過期的 handler；包一層，共用的 <see cref="ScriptedAgentUpstream"/> 不會被丟棄。</summary>
    private sealed class ForwardingHandler(HttpMessageHandler inner) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => new HttpMessageInvoker(inner, disposeHandler: false).SendAsync(request, cancellationToken);
    }
}

/// <summary>
/// 假的上游模型：依序回預先排好的 SSE；可以「等閘門打開才回」（測試排隊）或「一直等到被取消」（測試中斷）。記下每次請求的內容。
/// </summary>
public sealed class ScriptedAgentUpstream : HttpMessageHandler
{
    private readonly Queue<Func<CancellationToken, Task<HttpResponseMessage>>> replies = new();
    private readonly object gate = new();

    public List<string> Bodies { get; } = [];

    public TaskCompletionSource Started { get; private set; } = NewSource();

    public TaskCompletionSource<bool> Canceled { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Reset()
    {
        lock (gate)
        {
            replies.Clear();
            Bodies.Clear();
        }

        Started = NewSource();
        Canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Enqueue(string sse) => Add(_ => Task.FromResult(AiSse.Response(sse)));

    /// <summary>等回傳的閘門打開才回應。</summary>
    public TaskCompletionSource EnqueueGated(string sse)
    {
        var opened = NewSource();
        Add(async ct =>
        {
            await opened.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            return AiSse.Response(sse);
        });
        return opened;
    }

    /// <summary>一直等到請求被取消。</summary>
    public void EnqueueUntilCanceled() => Add(async ct =>
    {
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
            Canceled.TrySetResult(true);
            throw;
        }

        throw new InvalidOperationException("不會到這裡");
    });

    public async Task WaitForCallsAsync(int count)
    {
        for (var i = 0; i < 200; i++)
        {
            lock (gate)
            {
                if (Bodies.Count >= count)
                {
                    return;
                }
            }

            await Task.Delay(50);
        }

        throw new Xunit.Sdk.XunitException($"上游只收到 {Bodies.Count} 次呼叫，等不到第 {count} 次。");
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Func<CancellationToken, Task<HttpResponseMessage>> reply;
        lock (gate)
        {
            Bodies.Add(body);
            reply = replies.Count > 0 ? replies.Dequeue() : _ => Task.FromResult(AiSse.Response(AiSse.Responses(["（沒有排好的回覆）"])));
        }

        Started.TrySetResult();
        return await reply(cancellationToken);
    }

    private void Add(Func<CancellationToken, Task<HttpResponseMessage>> reply)
    {
        lock (gate)
        {
            replies.Enqueue(reply);
        }
    }

    private static TaskCompletionSource NewSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>假的分派：依序回排好的領域代碼（沒有排就回 monitoring），記下呼叫次數。</summary>
public sealed class FakeRouterClient : IAiChatCompletionClient
{
    public Queue<string> Codes { get; } = new();

    public int Calls { get; private set; }

    public void Reset()
    {
        Codes.Clear();
        Calls = 0;
    }

    public Task<AiAnalysisResult> CompleteAsync(AiChatCompletionRequest request, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(new AiAnalysisResult { Success = true, Markdown = Codes.Count > 0 ? Codes.Dequeue() : "monitoring" });
    }

    public IAsyncEnumerable<AiStreamUpdate> StreamAsync(AiChatCompletionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("分派只用非串流。");
}
