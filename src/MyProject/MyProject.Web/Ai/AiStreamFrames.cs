using MyProject.Dtos.Ai;

namespace MyProject.Web.Ai;

/// <summary>AI 服務的結果轉成串流回應的一行（0.10.5 起，WASM 遷移 M6；格式見 <see cref="AiStreamFrameDto"/>）。</summary>
public static class AiStreamFrames
{
    public static AiStreamFrameDto Start(AiStreamScopeDto? scope = null) => new() { Kind = AiStreamFrameKinds.Start, Scope = scope };

    public static AiStreamFrameDto Delta(string text) => new() { Kind = AiStreamFrameKinds.Delta, Text = text };

    public static AiStreamFrameDto Result(AiAnalysisResult result) => new() { Kind = AiStreamFrameKinds.Result, Result = ToDto(result) };

    public static AiResultDto ToDto(AiAnalysisResult result) => new()
    {
        Success = result.Success,
        Reason = result.Reason.ToString(),
        Markdown = result.Markdown,
        ErrorMessage = result.ErrorMessage,
        ErrorDetail = result.ErrorDetail,
        Usage = AiTokenUsage.ToDto(result.Usage),
        ModelName = result.ModelName,
        IsTruncatedByLength = result.IsTruncatedByLength,
        IsInterrupted = result.IsInterrupted,
        ElapsedMilliseconds = (long)result.Elapsed.TotalMilliseconds,
        IncludedEntryCount = result.Prompt.IncludedEntryCount,
        TotalEntryCount = result.Prompt.TotalEntryCount,
        DroppedByEntryLimit = result.Prompt.DroppedByEntryLimit,
        CharacterCount = result.Prompt.CharacterCount,
    };
}
