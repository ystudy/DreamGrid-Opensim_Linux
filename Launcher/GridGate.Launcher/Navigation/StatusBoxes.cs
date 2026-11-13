using Microsoft.AspNetCore.Razor.TagHelpers;

namespace GridGate.Launcher.Navigation;

/// <summary>
/// A status box a page shows only when it has something to say: <c>&lt;error-box text="@Model.Error" /&gt;</c> writes the box
/// when the text is not null and nothing otherwise. The text is encoded. It is an <c>object</c> because Razor turns a null
/// bound to a <c>string</c> attribute into "", which would show an empty box; an <c>object</c> keeps the null.
/// </summary>
public abstract class StatusBoxTagHelper(string tag, string classes, string role) : TagHelper
{
    [HtmlAttributeName("text")]
    public object? Text { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        if (Text is null)
        {
            output.SuppressOutput();
            return;
        }
        output.TagName = tag;
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", classes);
        output.Attributes.SetAttribute("role", role);
        output.Content.SetContent(Text.ToString());
    }
}

[HtmlTargetElement("error-box", TagStructure = TagStructure.WithoutEndTag)]
public sealed class ErrorBoxTagHelper() : StatusBoxTagHelper("div", "error-box error alert alert-danger", "alert");

[HtmlTargetElement("ok-box", TagStructure = TagStructure.WithoutEndTag)]
public sealed class OkBoxTagHelper() : StatusBoxTagHelper("div", "ok-box alert alert-success", "status");

[HtmlTargetElement("notice-box", TagStructure = TagStructure.WithoutEndTag)]
public sealed class NoticeBoxTagHelper() : StatusBoxTagHelper("p", "notice alert alert-info py-2", "status");
