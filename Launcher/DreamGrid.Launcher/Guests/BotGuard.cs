using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Guests;

/// <summary>The ids of the layers: what a failed <see cref="BotCheck"/> names and what the audit log calls them.</summary>
public static class BotLayers
{
    public const string Honeypot = "honeypot";
    public const string RateLimit = "rate-limit";
    public const string ProofOfWork = "proof-of-work";
    public const string ImageCaptcha = "image-captcha";

    /// <summary>What the owner's pages call a layer.</summary>
    public static string Title(string layer) => layer switch
    {
        Honeypot => "Honeypot",
        RateLimit => "Rate limit",
        ProofOfWork => "Proof of work",
        ImageCaptcha => "Image check",
        _ => layer,
    };
}

/// <summary>The outcome of <see cref="BotGuard.Check"/>: passed, or the id of the layer that stopped the post.</summary>
public sealed record BotCheck(bool Passed, string? Layer);

/// <summary>
/// Bot protection for the forms anonymous visitors can post (spec 7.6), a stack of layers the owner switches on and off. Each form
/// calls <see cref="Check"/> with its own name ("signup", "try"). The layers run in this order and the first to object decides:
/// <list type="number">
/// <item><see cref="BotLayers.Honeypot"/>: the hidden <c>website</c> field must be empty.</item>
/// <item><see cref="BotLayers.RateLimit"/>: at most <c>PerHourPerIp</c> passed posts from one address to one form in a sliding hour.</item>
/// <item><see cref="BotLayers.ProofOfWork"/>: the <c>pow</c> field carries a solved challenge, and each answer is accepted once.</item>
/// <item><see cref="BotLayers.ImageCaptcha"/>: the <c>captcha-token</c> field carries a token <see cref="ImageCaptcha"/> made, with
/// the letters or the sum right. A token gets one try, right or wrong, so the few possible sums can't be tried one after another.</item>
/// </list>
/// A post counts against the rate limit only when every layer
/// passed, so a visitor whose proof-of-work failed can try again. The counts and the used answers live in memory and are pruned as
/// they go stale, so a restart forgets them. A failed check logs the layer and the form, never what was typed. Safe from any thread.
/// </summary>
public sealed class BotGuard(Func<LauncherSettings> settings, ProofOfWork work, ImageCaptcha captcha, TimeProvider time, ILogger<BotGuard> log)
{
    /// <summary>The form fields <c>_BotFields</c> renders and <see cref="Check"/> reads.</summary>
    public const string HoneypotField = "website";
    public const string ProofField = "pow";
    public const string CaptchaTokenField = "captcha-token";
    public const string CaptchaLettersField = "captcha-letters";
    public const string CaptchaAnswerField = "captcha-answer";

    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly Lock _gate = new();
    private readonly Dictionary<(string Ip, string Form), Queue<DateTimeOffset>> _posts = [];
    private readonly Dictionary<string, DateTimeOffset> _used = [];
    private DateTimeOffset _swept;

    public BotCheck Check(HttpContext ctx, string form)
    {
        var bots = settings().Bots;
        var fields = ctx.Request.HasFormContentType ? ctx.Request.Form : null;
        var now = time.GetUtcNow();
        // One address is one address, whether it arrives as 1.2.3.4 or ::ffff:1.2.3.4.
        var ip = ctx.Connection.RemoteIpAddress is { } address ? (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString() : "unknown";

        lock (_gate)
        {
            Sweep(now);
            if (bots.Honeypot && !string.IsNullOrEmpty(fields?[HoneypotField].ToString()))
                return Stopped(BotLayers.Honeypot, form);

            Queue<DateTimeOffset>? posts = null;
            if (bots.RateLimit)
            {
                if (!_posts.TryGetValue((ip, form), out posts))
                    _posts[(ip, form)] = posts = new Queue<DateTimeOffset>();
                while (posts.Count > 0 && posts.Peek() <= now - Window) posts.Dequeue();
                if (posts.Count >= bots.PerHourPerIp) return Stopped(BotLayers.RateLimit, form);
            }

            if (bots.ProofOfWork)
            {
                if (!work.TryVerify(fields?[ProofField].ToString(), out var expires, out var solved))
                    return Stopped(BotLayers.ProofOfWork, form);
                // A solved challenge is used once, however its answer is written (spaces in the base64, JSON order): the key is the
                // verified challenge, which the signature binds, not the text that was posted.
                if (!_used.TryAdd(solved, expires)) return Stopped(BotLayers.ProofOfWork, form);
            }

            if (bots.ImageCaptcha)
            {
                var result = captcha.Check(fields?[CaptchaTokenField].ToString(), fields?[CaptchaLettersField].ToString(),
                    fields?[CaptchaAnswerField].ToString(), out var until, out var id);
                if (result == CaptchaResult.Invalid) return Stopped(BotLayers.ImageCaptcha, form);
                // A token gets one try: it is spent whether or not the answer was right, and it is named by the challenge the signature
                // binds (not by the posted text, which can be written another way). The prefix keeps it apart from the proof-of-work challenges in the same memory.
                if (!_used.TryAdd("captcha:" + id, until) || result != CaptchaResult.Right) return Stopped(BotLayers.ImageCaptcha, form);
            }

            posts?.Enqueue(now);
            return new BotCheck(true, null);
        }
    }

    private BotCheck Stopped(string layer, string form)
    {
        // Never the field values: the honeypot text and the proof-of-work payload are the visitor's to send, not ours to keep.
        log.LogWarning("Bot protection stopped a post to the {Form} form: {Layer}.", form, layer);
        return new BotCheck(false, layer);
    }

    /// <summary>Forgets what is stale, at most once a minute.</summary>
    private void Sweep(DateTimeOffset now)
    {
        if (now - _swept < TimeSpan.FromMinutes(1)) return;
        _swept = now;
        foreach (var key in _posts.Where(p => p.Value.Count == 0 || p.Value.Peek() <= now - Window).Select(p => p.Key).ToList())
        {
            var queue = _posts[key];
            while (queue.Count > 0 && queue.Peek() <= now - Window) queue.Dequeue();
            if (queue.Count == 0) _posts.Remove(key);
        }
        foreach (var key in _used.Where(u => u.Value <= now).Select(u => u.Key).ToList())
            _used.Remove(key);
    }
}
