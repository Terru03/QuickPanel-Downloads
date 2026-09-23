using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuickPanel.Models;

namespace QuickPanel.Services;

public sealed class ExternalAppRegistry
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
		Converters = { new JsonStringEnumConverter() }
	};

	private readonly string _builtInDefinitionsPath;

	private readonly string _userDefinitionsPath;

	private IReadOnlyList<ExternalAppDefinition>? _definitions;

	public ExternalAppRegistry(string? builtInDefinitionsPath = null, string? userDefinitionsPath = null)
	{
		_builtInDefinitionsPath = builtInDefinitionsPath ?? Path.Combine(AppContext.BaseDirectory, "external-apps.json");
		_userDefinitionsPath = userDefinitionsPath ?? Path.Combine(PortableDataPaths.DataDirectory, "external-apps.user.json");
	}

	public string UserDefinitionsPath => _userDefinitionsPath;

	public IReadOnlyList<ExternalAppDefinition> GetAll()
	{
		return _definitions ??= LoadDefinitions();
	}

	public ExternalAppDefinition? Find(string id)
	{
		return GetAll().FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
	}

	public void Reload()
	{
		_definitions = null;
	}

	public static IReadOnlyList<ExternalAppDefinition> Parse(string json, string sourceName = "external application definitions")
	{
		List<ExternalAppDefinition> definitions = JsonSerializer.Deserialize<List<ExternalAppDefinition>>(json, JsonOptions) ?? [];
		Validate(definitions, sourceName);
		return definitions;
	}

	private IReadOnlyList<ExternalAppDefinition> LoadDefinitions()
	{
		List<ExternalAppDefinition> combined = [];
		if (File.Exists(_builtInDefinitionsPath))
		{
			combined.AddRange(Parse(File.ReadAllText(_builtInDefinitionsPath), _builtInDefinitionsPath));
		}
		if (File.Exists(_userDefinitionsPath))
		{
			IReadOnlyList<ExternalAppDefinition> userDefinitions = Parse(File.ReadAllText(_userDefinitionsPath), _userDefinitionsPath);
			foreach (ExternalAppDefinition definition in userDefinitions)
			{
				combined.RemoveAll(item => item.Id.Equals(definition.Id, StringComparison.OrdinalIgnoreCase));
				combined.Add(definition);
			}
		}
		Validate(combined, "merged external application registry");
		return combined.Where(item => item.IsEnabled).ToList();
	}

	private static void Validate(IReadOnlyList<ExternalAppDefinition> definitions, string sourceName)
	{
		List<string> errors = [];
		foreach (ExternalAppDefinition definition in definitions)
		{
			errors.AddRange(definition.Validate());
		}
		foreach (IGrouping<string, ExternalAppDefinition> duplicate in definitions.GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
		{
			errors.Add($"Duplicate external application ID '{duplicate.Key}'.");
		}
		if (errors.Count > 0)
		{
			throw new InvalidDataException($"Invalid {sourceName}:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
		}
	}
}
