using System.Text;

namespace Soundstage.Core.Apo;

/// <summary>
/// An ordered list of APO commands representing one config file. Rendering assigns
/// sequential <c>Filter N:</c> numbers, restarting at 1 after every <c>Device:</c> line
/// (the numbering convention Peace and AutoEq exports use — cosmetic for APO itself,
/// but it keeps generated files diffable and familiar).
/// </summary>
public sealed class ApoDocument
{
    public ApoDocument()
    {
    }

    public ApoDocument(IEnumerable<ApoCommand> commands)
    {
        Commands.AddRange(commands);
    }

    public List<ApoCommand> Commands { get; } = [];

    /// <summary>Line terminator used for generated files; APO accepts both, CRLF matches the platform.</summary>
    public const string LineEnding = "\r\n";

    public IReadOnlyList<string> RenderLines()
    {
        var lines = new List<string>(Commands.Count);
        var filterNumber = 0;
        foreach (var command in Commands)
        {
            switch (command)
            {
                case DeviceCommand device:
                    filterNumber = 0;
                    lines.Add(device.Render());
                    break;
                case FilterCommand filter:
                    filterNumber++;
                    lines.Add(filter.RenderNumbered(filterNumber));
                    break;
                default:
                    lines.Add(command.Render());
                    break;
            }
        }

        return lines;
    }

    /// <summary>Full file text, terminated with a trailing newline.</summary>
    public string Render()
    {
        var sb = new StringBuilder();
        foreach (var line in RenderLines())
        {
            sb.Append(line).Append(LineEnding);
        }

        return sb.ToString();
    }
}
