using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MyProject.AccessDatas.Models;

namespace MyProject.Business.Services.Other.AuditIntegrity;

/// <summary>兩條鏈的代號（0.9.175 起）。瀏覽類與其他保留天數不同，放同一條鏈的話刪中間的瀏覽紀錄會讓鏈斷掉。</summary>
public static class AuditChains
{
    public const int Change = 0;
    public const int Browsing = 1;

    public static readonly int[] All = [Change, Browsing];

    public static int Of(string action) => Helpers.AuditActions.IsBrowsing(action) ? Browsing : Change;

    public static string Name(int chain) => chain == Browsing ? "瀏覽鏈" : "異動鏈";
}

/// <summary>
/// 稽核雜湊鏈第 1 版的簽章格式（0.9.175 起）。
///
/// ⚠️ <b>格式一旦上線就不能改</b>：已經簽好的紀錄會全部驗不過。要改就另寫 V2，並在資料列記版本。
/// 由 <c>AuditHashV1Tests</c> 的固定答案釘住。
///
/// 每個欄位寫成「UTF-8 位元組長度 + ':' + 位元組 + '\n'」，null 寫成「-\n」，所以 null 與空字串、
/// 欄位之間的邊界都不會混淆。字串一律用 <see cref="Encoding.UTF8"/>（無效的孤立 surrogate 會換成 U+FFFD，
/// 和 SQLite 讀回來的值相同），<b>不可</b>改用會拋例外的編碼器：任何人都能在搜尋字串塞這種字元製造假警報。
/// 時間只用 Ticks：SQLite 讀回來的 Kind 是 Unspecified，Ticks 則完全不變。
/// </summary>
public static class AuditHashV1
{
    /// <summary>全新的鏈第一筆的「上一筆雜湊」。</summary>
    public static readonly string Genesis = new('0', 64);

    public static string ComputeRowHash(byte[] key, AuditLog row)
    {
        var writer = new CanonicalWriter("audit-row-v1");
        writer.Add(row.Chain);
        writer.Add(row.Seq);
        writer.Add(row.PrevHash);
        writer.Add(row.OccurredAt.Ticks);
        writer.Add(row.ActorUserId);
        writer.Add(row.ActorAccount);
        writer.Add(row.Action);
        writer.Add(row.TargetType);
        writer.Add(row.TargetId);
        writer.Add(row.Detail);
        writer.Add(row.Success ? "1" : "0");
        writer.Add(row.ClientIp);
        writer.Add(row.ContentFile);
        writer.Add(row.ContentHash);
        writer.Add(row.KeyId);
        return writer.Mac(key);
    }

    public static string ComputeHeadMac(byte[] key, AuditChainHead head)
    {
        var writer = new CanonicalWriter("audit-head-v1");
        writer.Add(head.Chain);
        writer.Add(head.StartSeq);
        writer.Add(head.StartPrevHash);
        writer.Add(head.LastSeq);
        writer.Add(head.LastHash);
        writer.Add(head.KeyId);
        writer.Add(head.KeySinceSeq);
        writer.Add(head.LegacyMaxId);
        writer.Add(head.CreatedAt.Ticks);
        writer.Add(head.RebuiltAt?.Ticks);
        return writer.Mac(key);
    }

    /// <summary>金鑰代號：不直接用金鑰的雜湊，避免代號洩漏任何金鑰資訊。</summary>
    public static string ComputeKeyId(byte[] key)
        => Convert.ToHexStringLower(HMACSHA256.HashData(key, "audit-key-id"u8))[..16];

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class CanonicalWriter
    {
        private readonly MemoryStream buffer = new();

        public CanonicalWriter(string domain) => Add(domain);

        public void Add(long? value) => Add(value?.ToString(CultureInfo.InvariantCulture));

        public void Add(string? value)
        {
            if (value is null)
            {
                buffer.Write("-\n"u8);
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(value);
            buffer.Write(Encoding.ASCII.GetBytes(bytes.Length.ToString(CultureInfo.InvariantCulture)));
            buffer.WriteByte((byte)':');
            buffer.Write(bytes);
            buffer.WriteByte((byte)'\n');
        }

        public string Mac(byte[] key) => Convert.ToHexStringLower(HMACSHA256.HashData(key, buffer.ToArray()));
    }
}
