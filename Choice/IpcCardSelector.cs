using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunicateTheSpire2.Protocol;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.TestSupport;

namespace CommunicateTheSpire2.Choice;

public sealed class IpcCardSelector : ICardSelector
{
	public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect)
	{
		var list = options.ToList();
		if (list.Count == 0)
			return Task.FromResult(Enumerable.Empty<CardModel>());

		var choiceOptions = list.Select((c, i) => new ChoiceOptionSummary
		{
			index = i,
			id = c.Id.Entry,
			name = SafeGetName(c)
		}).ToList();

		return GetSelectedByIndicesAsync(choiceOptions, list, minSelect, maxSelect, null);
	}

	public CardModel? GetSelectedCardReward(IReadOnlyList<CardCreationResult> options, IReadOnlyList<CardRewardAlternative> alternatives)
	{
		if (options.Count == 0)
			return null;

		var choiceOptions = new List<ChoiceOptionSummary>();
		for (int i = 0; i < options.Count; i++)
		{
			var c = options[i].Card;
			choiceOptions.Add(new ChoiceOptionSummary
			{
				index = i,
				id = c.Id.Entry,
				name = SafeGetName(c)
			});
		}

		var altNames = alternatives.Select(a => a.OptionId).ToList();
		var result = IpcChoiceBridge.RequestChoiceSync("card_reward", choiceOptions, 0, 1, altNames);

		if (result.Skip || result.Indices.Length == 0)
			return null;

		int idx = result.Indices[0];
		if (idx < 0 || idx >= options.Count)
			return null;

		return options[idx].Card;
	}

	private static async Task<IEnumerable<CardModel>> GetSelectedByIndicesAsync(
		List<ChoiceOptionSummary> choiceOptions,
		List<CardModel> cards,
		int minSelect,
		int maxSelect,
		IReadOnlyList<string>? alternatives)
	{
		var result = await IpcChoiceBridge.RequestChoiceAsync("card_select", choiceOptions, minSelect, maxSelect, alternatives);

		if (result.Skip)
			return Array.Empty<CardModel>();

		var selected = new List<CardModel>();
		foreach (int idx in result.Indices)
		{
			if (idx >= 0 && idx < cards.Count)
				selected.Add(cards[idx]);
		}
		return selected;
	}

	private static string SafeGetName(CardModel card)
	{
		try
		{
			return card.Title ?? card.Id?.Entry ?? "";
		}
		catch
		{
			return card.Id?.Entry ?? "";
		}
	}
}
