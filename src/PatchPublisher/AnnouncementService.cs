using System.Text.Json;
using System.Diagnostics;

namespace GamePatchDepot;

public sealed class AnnouncementService(R2ObjectStore store, SigningService signer)
{
    public const string CurrentKey = "announcements/current.json";
    public const int EnvelopeLimit = 1024 * 1024;

    public static void Validate(AnnouncementFeed feed, bool allowEmpty = false)
    {
        if (feed.SchemaVersion != 1 || feed.Product != "NovaStoryAnnouncements" ||
            feed.Revision < (allowEmpty ? 0 : 1) || feed.Revision > 9007199254740991 || feed.Items is null || feed.Items.Count > 200 ||
            feed.Items.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() != feed.Items.Count)
            throw new PatchPublisherException("公告清單格式或版次不符規範。");
        foreach (AnnouncementEntry entry in feed.Items)
        {
            ValidateDraft(new() { Id = entry.Id, Title = entry.Title, Body = entry.Body, Category = entry.Category });
            if (entry.Revision < 1 || entry.Revision > 9007199254740991 || entry.ReadRevision < 1 || entry.ReadRevision > entry.Revision || entry.PublishedAt <= DateTimeOffset.UnixEpoch)
                throw new PatchPublisherException("公告版次或發布時間無效。");
        }
    }

    public static void ValidateDraft(AnnouncementDraft draft)
    {
        if (draft.Id is null || draft.Id.Length != 32 || draft.Id.Any(c => !char.IsAsciiHexDigitLower(c)) ||
            string.IsNullOrWhiteSpace(draft.Title) || draft.Title.Length > 120 ||
            string.IsNullOrWhiteSpace(draft.Body) || draft.Body.Length > 16000 ||
            draft.Category is not ("notice" or "maintenance" or "update") ||
            draft.Title.Any(char.IsControl) || draft.Body.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            throw new PatchPublisherException("請填寫標題（最多 120 字）、內容（最多 16,000 字）與有效公告分類。");
    }

    public static AnnouncementFeed Edit(AnnouncementFeed current, AnnouncementDraft draft, bool withdraw)
    {
        Validate(current, true);
        ValidateDraft(draft);
        AnnouncementEntry? old = current.Items.SingleOrDefault(e => e.Id == draft.Id);
        if (withdraw && old is null) throw new PatchPublisherException("公告不存在，無法下架。");
        if (withdraw) draft = draft with { Title = old!.Title, Body = old.Body, Category = old.Category, Popup = old.Popup, RemindAgain = false };
        long revision = checked((old?.Revision ?? 0) + 1);
        AnnouncementEntry entry = new()
        {
            Id = draft.Id, Revision = revision,
            ReadRevision = old is null || draft.RemindAgain ? revision : old.ReadRevision,
            Title = draft.Title, Body = draft.Body, Category = draft.Category,
            Popup = draft.Popup, Withdrawn = withdraw, PublishedAt = DateTimeOffset.UtcNow
        };
        AnnouncementFeed next = current with
        {
            Revision = checked(current.Revision + 1),
            Items = [entry, .. current.Items.Where(e => e.Id != draft.Id)]
        };
        Validate(next);
        return next;
    }

    private async Task<(AnnouncementFeed Feed, string? Etag)> ReadAsync(CancellationToken token)
    {
        ObjectMetadata? before = await store.HeadAsync(CurrentKey, token);
        byte[]? bytes = await store.GetBoundedBytesOrNullAsync(CurrentKey, EnvelopeLimit, token);
        ObjectMetadata? after = await store.HeadAsync(CurrentKey, token);
        if (before?.ETag != after?.ETag || (bytes is null) != (after is null))
            throw new PatchPublisherException("公告在讀取時已變更，請重新整理。");
        if (bytes is null) return (new(), null);
        using JsonDocument envelope = JsonDocument.Parse(bytes);
        if (envelope.RootElement.GetProperty("schemaVersion").GetInt32() != 1)
            throw new PatchPublisherException("公告封套版本不符。");
        AnnouncementFeed feed = SignedEnvelopeService.Open(bytes, AnnouncementJsonContext.Default.AnnouncementFeed, signer);
        Validate(feed);
        return (feed, after!.ETag);
    }

    public async Task<AnnouncementFeed> LoadAsync(CancellationToken token) => (await ReadAsync(token)).Feed;

    public async Task<AnnouncementFeed> PublishAsync(AnnouncementDraft draft, long expectedRevision, bool withdraw, CancellationToken token)
    {
        Stopwatch stage = Stopwatch.StartNew();
        void LogStage(string message)
        {
            Console.WriteLine($"公告：{message}（{stage.Elapsed.TotalSeconds:F2} 秒）");
            stage.Restart();
        }
        Console.WriteLine("公告：正在讀取遠端清單並驗章…");
        (AnnouncementFeed current, string? etag) = await ReadAsync(token);
        LogStage("遠端清單讀取與驗章完成");
        if (current.Revision != expectedRevision) throw new PatchPublisherException("遠端公告已變更，請重新整理後再發布，未覆寫。");
        AnnouncementFeed next = Edit(current, draft, withdraw);
        byte[] bytes = SignedEnvelopeService.Create(next, AnnouncementJsonContext.Default.AnnouncementFeed, signer);
        Validate(SignedEnvelopeService.Open(bytes, AnnouncementJsonContext.Default.AnnouncementFeed, signer));
        if (bytes.Length > EnvelopeLimit) throw new PatchPublisherException("公告清單超過 1 MiB，未發布。");
        LogStage("新清單簽章與本機驗章完成");
        string history = $"announcements/history/{next.Revision}-{FileUtilities.Sha256Hex(bytes)}.json";
        Console.WriteLine("公告：正在上傳歷史紀錄…");
        await store.PutBytesImmutableAsync(history, bytes, "application/json", "public, max-age=31536000, immutable", token);
        LogStage("歷史紀錄上傳完成");
        byte[] readBack = await store.GetBoundedBytesOrNullAsync(history, EnvelopeLimit, token)
            ?? throw new PatchPublisherException("公告紀錄回驗失敗。");
        if (!readBack.AsSpan().SequenceEqual(bytes)) throw new PatchPublisherException("公告紀錄內容回驗不符，未切換。");
        LogStage("歷史紀錄下載回驗完成");
        Console.WriteLine("公告：正在切換目前清單…");
        try { await store.PutBytesConditionalAsync(CurrentKey, bytes, "application/json", "no-store, no-cache, max-age=0", etag, etag is null, token); }
        catch (ConditionalWriteException) { throw new PatchPublisherException("公告發布衝突，請重新整理，未覆寫其他人的內容。"); }
        LogStage("目前清單切換完成");
        // A transport failure can occur after commit; callers must refresh instead of claiming rollback.
        AnnouncementFeed confirmed = await LoadAsync(token);
        if (confirmed.Revision != next.Revision) throw new PatchPublisherException("發布結果待確認，請重新整理公告。");
        LogStage("遠端結果確認完成");
        return confirmed;
    }
}
