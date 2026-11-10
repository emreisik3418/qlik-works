namespace QlikSheetCleaner.Qlik;

/// <summary>X-Qlik-User header'ında gönderilen kimlik.</summary>
public sealed record QlikIdentity(string UserDirectory, string UserId)
{
    public string ToHeaderValue() => $"UserDirectory={UserDirectory}; UserId={UserId}";

    public override string ToString() => $"{UserDirectory}\\{UserId}";

    public bool Matches(QlikIdentity other) =>
        string.Equals(UserDirectory, other.UserDirectory, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(UserId, other.UserId, StringComparison.OrdinalIgnoreCase);
}
