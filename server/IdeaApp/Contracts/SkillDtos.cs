namespace IdeaApp.Contracts;

/// <summary>分发副本的清单：版本、入口、文件清单。</summary>
public sealed record SkillManifestDto(string Version, string Entry, SkillFileDto[] Files);

/// <summary>分发副本里的一个文件。</summary>
public sealed record SkillFileDto(string Path, string Sha256, long Size);
