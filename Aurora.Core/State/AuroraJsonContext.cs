using Aurora.Core.Models; // Ensure this is included
using System.Text.Json.Serialization;
using Aurora.Core.Contract;

namespace Aurora.Core.State;

[JsonSourceGenerationOptions(WriteIndented = true)] 
[JsonSerializable(typeof(List<Package>))]
[JsonSerializable(typeof(Dictionary<string, Package>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Package))]
[JsonSerializable(typeof(AuroraManifest))] // Add this if you plan to serialize manifests to JSON
[JsonSerializable(typeof(RepoConfig))]
[JsonSerializable(typeof(Repository))]
public partial class AuroraJsonContext : JsonSerializerContext { }
