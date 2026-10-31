namespace DreamGrid.Launcher.Core.Email;

/// <summary>An email address as the main log may show it: the first letter and the domain (<c>a…@example.com</c>).</summary>
public static class EmailMask
{
    public static string Of(string? address)
    {
        var at = address?.LastIndexOf('@') ?? -1;
        if (address is null || at <= 0 || at == address.Length - 1) return "…";
        return address[0] + "…" + address[at..];
    }
}
