using System.Runtime.CompilerServices;
using System.Text.Json;
using QuickPanel.Models;
using QuickPanel.Services;

internal static class BrowserProfileRegistryTests
{
	[ModuleInitializer]
	internal static void Run()
	{
		string root = Path.Combine(Path.GetTempPath(), "QuickPanel.Tests", "profiles-" + Guid.NewGuid().ToString("N"));
		string registryPath = Path.Combine(root, "profiles.json");
		string webView2Path = Path.Combine(root, "WebView2");
		Directory.CreateDirectory(root);
		try
		{
			BrowserProfileRegistryService service = new(registryPath, webView2Path);
			AiTab existingProfileTab = new()
			{
				Id = "custom:existing",
				Name = "ChatGPT Work",
				Url = "https://chatgpt.com",
				BrowserProfileId = "qp-existing",
				BrowserProfileLabel = "Profile 2",
				IsCustom = true
			};

			IReadOnlyList<BrowserProfileDefinition> migrated = service.Synchronize(new[] { existingProfileTab });
			Need(migrated.Count == 2, "Existing 2.4.2 profile was not discovered exactly once.");
			Need(migrated.Any(profile => profile.Id == "default" && profile.IsDefault), "Default profile was not created.");
			Need(migrated.Any(profile => profile.Id == "qp-existing" && profile.Name == "Profile 2"), "Existing profile ID/label was not preserved.");

			BrowserProfileDefinition renamed = service.Rename("qp-existing", "Work");
			Need(renamed.Id == "qp-existing" && renamed.Name == "Work", "Renaming changed the stable profile identity.");
			Need(service.Synchronize(new[] { existingProfileTab }).Single(profile => profile.Id == "qp-existing").Name == "Work",
				"Tab's stale label overwrote the managed profile name.");

			string orphanProfilePath = Path.Combine(webView2Path, "EBWebView", "qp-orphan123");
			Directory.CreateDirectory(orphanProfilePath);
			File.WriteAllText(Path.Combine(orphanProfilePath, "marker.txt"), "orphan profile");
			IReadOnlyList<BrowserProfileDefinition> withOrphan = service.Synchronize(Array.Empty<AiTab>());
			Need(withOrphan.Any(profile => profile.Id == "qp-orphan123"),
				"An existing on-disk 2.4.2 WebView2 profile with no remaining tab was not recovered for management.");

			BrowserProfileDefinition personal = service.Create("Personal");
			Need(personal.Id.StartsWith("qp-", StringComparison.Ordinal) && personal.Name == "Personal", "Named profile creation failed.");
			BrowserProfileDefinition work = service.Create("  Work  ");
			Need(work.Name == "Work", "Explicit profile names were not normalized without changing their meaning.");
			IReadOnlyList<BrowserProfileDefinition> sharedUse = service.Synchronize(new[]
			{
				existingProfileTab,
				new AiTab
				{
					Id = "custom:existing-duplicate",
					Name = "ChatGPT Work duplicate",
					Url = "https://chatgpt.com",
					BrowserProfileId = "QP-EXISTING",
					BrowserProfileLabel = "Stale duplicate label",
					IsCustom = true
				}
			});
			Need(sharedUse.Count(profile => profile.Id.Equals("qp-existing", StringComparison.OrdinalIgnoreCase)) == 1 &&
				sharedUse.Single(profile => profile.Id.Equals("qp-existing", StringComparison.OrdinalIgnoreCase)).Name == "Work",
				"Multiple tabs sharing one profile changed or duplicated its stable identity.");
			service.SetDefault(personal.Id);
			IReadOnlyList<BrowserProfileDefinition> afterDefault = service.GetProfiles();
			Need(afterDefault.Count(profile => profile.IsDefault) == 1 && afterDefault.Single(profile => profile.IsDefault).Id == personal.Id,
				"Setting a managed profile as default did not preserve exactly one default.");

			Need(BrowserProfileRegistryService.GetBrowserProfileId("default") is null,
				"Registry default must map to the legacy WebView2 Default profile without creating another browser profile.");
			Need(BrowserProfileRegistryService.GetRegistryId(null) == "default",
				"Legacy default browser tabs did not map to the managed Default identity.");
			NeedThrows(() => service.Delete(personal.Id), "The default managed profile could be deleted.");
			service.SetDefault("default");
			NeedThrows(() => service.Delete("default"), "The legacy shared Default profile could be deleted.");

			string personalData = service.GetProfileDataPath(personal.Id);
			Directory.CreateDirectory(personalData);
			File.WriteAllText(Path.Combine(personalData, "marker.txt"), "profile data");
			service.Delete(personal.Id);
			Need(service.GetProfiles().All(profile => profile.Id != personal.Id), "Deleted profile remained in registry.");
			Need(!Directory.Exists(personalData), "Deleted unused profile browser data remained on disk.");

			service.Delete("qp-orphan123");
			Need(!Directory.Exists(orphanProfilePath), "Recovered orphan profile could not be deleted.");

			string lockedData = service.GetProfileDataPath(work.Id);
			Directory.CreateDirectory(lockedData);
			string lockedMarker = Path.Combine(lockedData, "locked.db");
			File.WriteAllText(lockedMarker, "locked profile sentinel");
			using (FileStream lockStream = new(lockedMarker, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
			{
				service.Delete(work.Id);
				Need(service.GetProfiles().All(profile => profile.Id != work.Id),
					"A profile pending locked-data cleanup remained selectable.");
				using JsonDocument pendingJson = JsonDocument.Parse(File.ReadAllText(registryPath));
				Need(pendingJson.RootElement.GetProperty("PendingDeletionProfileIds")
					.EnumerateArray().Any(item => item.GetString() == work.Id),
					"Locked profile data was not recorded for deferred deletion.");
			}
			service.ProcessPendingDeletions();
			Need(!Directory.Exists(lockedData), "Deferred profile cleanup did not remove unlocked profile data.");
			using JsonDocument cleanedJson = JsonDocument.Parse(File.ReadAllText(registryPath));
			Need(!cleanedJson.RootElement.GetProperty("PendingDeletionProfileIds").EnumerateArray().Any(),
				"Completed deferred profile deletion remained pending after restart cleanup.");

			using JsonDocument json = JsonDocument.Parse(File.ReadAllText(registryPath));
			Need(json.RootElement.TryGetProperty("Profiles", out JsonElement profilesElement) && profilesElement.ValueKind == JsonValueKind.Array,
				"Profile registry does not persist the expected Profiles array.");
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	private static void Need(bool condition, string message)
	{
		if (!condition)
		{
			throw new InvalidOperationException(message);
		}
	}

	private static void NeedThrows(Action action, string message)
	{
		try
		{
			action();
		}
		catch (InvalidOperationException)
		{
			return;
		}
		throw new InvalidOperationException(message);
	}
}
