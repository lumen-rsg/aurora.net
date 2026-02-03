using System.Text.Json.Serialization;
using Aurora.Core.Models;

namespace Aurora.RepoTool;

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Repository))]
[JsonSerializable(typeof(RepoPackage))]
[JsonSerializable(typeof(List<string>))]
internal partial class RepoContext : JsonSerializerContext
{
}