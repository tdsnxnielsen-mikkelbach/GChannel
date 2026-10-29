namespace GChannel.Web;

/// <summary>A value/label pair for <c>RadzenDropDown</c> option lists (<c>ValueProperty="Value" TextProperty="Text"</c>).</summary>
public sealed record SelectOption(string Value, string Text);
