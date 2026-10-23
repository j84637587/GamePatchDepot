namespace GamePatchDepot;

public static class LauncherTicketPolicy
{
    // Published NovaLink 1.1.0 predates the ready-stdin handoff.
    public static void ValidateMinimumVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Split('.').Length != 3 ||
            !Version.TryParse(value, out Version? version) || version.ToString() != value || version <= new Version(1, 1, 0))
            throw new PatchPublisherException("請填寫已發布、支援新交接的最低 NovaLink 版本（x.y.z，必須高於不支援的 1.1.0），不可沿用舊版或預設值。");
    }

    public static void ValidateAgainstBase(string? value, string? baseMinimum)
    {
        ValidateMinimumVersion(value);
        if (Version.TryParse(baseMinimum, out Version? inherited) && Version.Parse(value!) < inherited)
            throw new PatchPublisherException($"最低 NovaLink 版本不可低於基準版本要求的 {baseMinimum}。");
    }
}
