namespace RustedShpizhionStudio.Core;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(GitHubRelease))]
[JsonSerializable(typeof(List<GitHubRelease>))]
[JsonSerializable(typeof(UpdateManifest))]
[JsonSerializable(typeof(UpdateHistoryEntry))]
[JsonSerializable(typeof(List<UpdateHistoryEntry>))]
internal partial class AppJsonContext : JsonSerializerContext
{
}
