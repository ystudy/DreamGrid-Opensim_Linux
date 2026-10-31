using System.Globalization;
using System.Text;

namespace DreamGrid.Launcher.Core.Guests;

/// <summary>
/// The stroke font the image CAPTCHA draws with: each character is a few polylines on a grid 4 wide and 6 tall (y grows downward).
/// The alphabet leaves out the characters that are easy to mix up (B and 8, I and 1 and L, O and 0, S and 5, Z and 2, G and Q).
/// Drawing strokes instead of using a <c>&lt;text&gt;</c> element keeps the answer out of the markup.
/// </summary>
internal static class CaptchaGlyphs
{
    public const string Alphabet = "ACDEFHJKMNPRTUVWXY34679";

    /// <summary>Width and height of a glyph, in grid units.</summary>
    public const double Width = 4, Height = 6;

    // Polylines are separated by ';', points by spaces, a point is "x,y".
    private static readonly Dictionary<char, double[][][]> Strokes = new()
    {
        ['A'] = Parse("0,6 2,0 4,6; 0.7,4 3.3,4"),
        ['C'] = Parse("4,1 3,0 1,0 0,1 0,5 1,6 3,6 4,5"),
        ['D'] = Parse("0,0 0,6 2,6 4,4 4,2 2,0 0,0"),
        ['E'] = Parse("4,0 0,0 0,6 4,6; 0,3 3,3"),
        ['F'] = Parse("4,0 0,0 0,6; 0,3 3,3"),
        ['H'] = Parse("0,0 0,6; 4,0 4,6; 0,3 4,3"),
        ['J'] = Parse("4,0 4,5 3,6 1,6 0,5"),
        ['K'] = Parse("0,0 0,6; 4,0 0,3 4,6"),
        ['M'] = Parse("0,6 0,0 2,3 4,0 4,6"),
        ['N'] = Parse("0,6 0,0 4,6 4,0"),
        ['P'] = Parse("0,6 0,0 3,0 4,1 4,2 3,3 0,3"),
        ['R'] = Parse("0,6 0,0 3,0 4,1 4,2 3,3 0,3; 2,3 4,6"),
        ['T'] = Parse("0,0 4,0; 2,0 2,6"),
        ['U'] = Parse("0,0 0,5 1,6 3,6 4,5 4,0"),
        ['V'] = Parse("0,0 2,6 4,0"),
        ['W'] = Parse("0,0 1,6 2,2 3,6 4,0"),
        ['X'] = Parse("0,0 4,6; 4,0 0,6"),
        ['Y'] = Parse("0,0 2,3 4,0; 2,3 2,6"),
        ['3'] = Parse("0,1 1,0 3,0 4,1 4,2 3,3 1,3; 3,3 4,4 4,5 3,6 1,6 0,5"),
        ['4'] = Parse("3,6 3,0 0,4 4,4"),
        ['6'] = Parse("4,1 3,0 1,0 0,1 0,5 1,6 3,6 4,5 4,4 3,3 1,3 0,4"),
        ['7'] = Parse("0,0 4,0 1,6"),
        ['9'] = Parse("0,5 1,6 3,6 4,5 4,1 3,0 1,0 0,1 0,2 1,3 3,3 4,2"),
    };

    public static bool Has(char c) => Strokes.ContainsKey(c);

    /// <summary>
    /// The SVG path data for <paramref name="c"/>, each grid unit drawn as <paramref name="scale"/> user units, with the point
    /// (<paramref name="wobble"/> returns a value in -1..1 for each coordinate) nudged by up to <paramref name="jitter"/> units.
    /// </summary>
    public static string Path(char c, double scale, double jitter, Func<double> wobble)
    {
        var d = new StringBuilder();
        foreach (var line in Strokes[c])
        {
            for (var i = 0; i < line.Length; i++)
            {
                d.Append(i == 0 ? 'M' : 'L')
                 .Append(Number((line[i][0] + jitter * wobble()) * scale)).Append(' ')
                 .Append(Number((line[i][1] + jitter * wobble()) * scale));
            }
        }
        return d.ToString();
    }

    private static string Number(double v) => Math.Round(v, 1).ToString("0.#", CultureInfo.InvariantCulture);

    private static double[][][] Parse(string text) =>
        [.. text.Split(';', StringSplitOptions.TrimEntries).Select(line =>
            line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(p =>
            {
                var xy = p.Split(',');
                return new[] { double.Parse(xy[0], CultureInfo.InvariantCulture), double.Parse(xy[1], CultureInfo.InvariantCulture) };
            }).ToArray())];
}
