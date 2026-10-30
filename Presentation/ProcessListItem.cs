using System.Windows.Media;

namespace CompactLoader;

// Presentation data stays separate from the process model used by the loader.
internal sealed record ProcessListItem(Target Target, ImageSource Icon)
{
    public int Id => Target.Id;
    public string Name => string.IsNullOrWhiteSpace(Target.WindowTitle) ? Target.Name : $"{Target.Name} — {Target.WindowTitle}";
    public string Architecture => Target.Architecture;
}
