using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;

namespace CommunicateTheSpire2.Patches;

/// <summary>
/// When our mod is loaded, add a "Configure CommunicateTheSpire2" button to GeneralSettings.
/// </summary>
[HarmonyPatch(typeof(NSettingsScreen), nameof(NSettingsScreen._Ready))]
public static class PatchNSettingsScreenAddConfigButton
{
	[HarmonyPostfix]
	public static void Postfix(NSettingsScreen __instance)
	{
		if (!ModManager.LoadedMods.Any(m => (m.pckName ?? "").Contains("CommunicateTheSpire2")))
			return;

		var general = __instance.GetNodeOrNull<NSettingsPanel>("%GeneralSettings");
		if (general?.Content == null)
			return;

		var btn = new Button
		{
			Text = "Configure CommunicateTheSpire2",
			CustomMinimumSize = new Vector2(280, 36)
		};
		btn.Pressed += () =>
		{
			var modal = new CommunicateTheSpire2.Ui.NCtS2ConfigModal();
			NModalContainer.Instance?.Add(modal);
		};
		general.Content.AddChild(btn);
	}
}
