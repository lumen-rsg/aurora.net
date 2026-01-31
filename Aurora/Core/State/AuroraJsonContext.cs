using System.Text.Json.Serialization;
using Aurora.Core.Models;

namespace Aurora.Core.State;

[JsonSourceGenerationOptions(WriteIndented = true)] // Pretty print JSON
[JsonSerializable(typeof(List<Package>))]
[JsonSerializable(typeof(Dictionary<string, Package>))]
[JsonSerializable(typeof(List<string>))] // For the journal
internal partial class AuroraJsonContext : JsonSerializerContext
{
}