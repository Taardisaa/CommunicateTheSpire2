using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using CommunicateTheSpire2.Choice;

namespace CommunicateTheSpire2.Patches;

/// <summary>
/// When IpcCardSelector is active, after the card reward screen is shown we send choice_request
/// and register a simulation callback. When CHOOSE_RESPONSE arrives we simulate a click on the
/// chosen card (EmitSignal Pressed) instead of bypassing the UI.
/// </summary>
[HarmonyPatch(typeof(NCardRewardSelectionScreen), nameof(NCardRewardSelectionScreen.AfterOverlayOpened))]
public static class PatchNCardRewardSelectionScreenAfterOverlayOpened
{
	[HarmonyPostfix]
	public static void Postfix(NCardRewardSelectionScreen __instance)
	{
		var selector = CardSelectCmd.Selector;
		if (selector == null || selector.GetType().FullName != "CommunicateTheSpire2.Choice.IpcCardSelector")
			return;

		var holders = UiHelper.FindAll<NCardHolder>(__instance).ToList();
		if (holders.Count == 0)
			return;

		var choiceOptions = new List<ChoiceOptionSummary>();
		for (int i = 0; i < holders.Count; i++)
		{
			var h = holders[i];
			try
			{
				var card = h.CardModel;
				choiceOptions.Add(new ChoiceOptionSummary
				{
					index = i,
					id = card?.Id?.Entry ?? "",
					name = card?.Title ?? card?.Id?.Entry ?? ""
				});
			}
			catch
			{
				choiceOptions.Add(new ChoiceOptionSummary { index = i, id = "", name = "" });
			}
		}

		IpcChoiceBridge.RequestChoiceSimulation(
			"card_reward",
			choiceOptions,
			minSelect: 0,
			maxSelect: 1,
			alternatives: null,
			onResponse: (indices, skip) => global::CommunicateTheSpire2.ModEntry.DeferSimulateCardRewardClick(indices, skip));
	}
}
