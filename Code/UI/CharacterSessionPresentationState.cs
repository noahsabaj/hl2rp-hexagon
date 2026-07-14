#nullable enable

using System.Collections.Immutable;
using Hexagon.V2.Domain;

namespace HL2RP.UI;

/// <summary>
/// Local presentation choices that belong to one active character lifetime.
/// None of this state may flow into the next character selected on the same
/// authenticated connection.
/// </summary>
public sealed class CharacterSessionPresentationState
{
	public ShowcaseWorkspace Workspace { get; set; }
	public bool ScoreboardOpen { get; set; }
	public ImmutableArray<NotificationViewModel> Notifications { get; set; } =
		ImmutableArray<NotificationViewModel>.Empty;
	public long DismissedItemPresentationSequence { get; set; }
	public ItemId? SelectedNoteItemId { get; set; }
	public ItemId? SelectedRadioItemId { get; set; }

	public void Reset()
	{
		Workspace = ShowcaseWorkspace.None;
		ScoreboardOpen = false;
		Notifications = ImmutableArray<NotificationViewModel>.Empty;
		DismissedItemPresentationSequence = 0;
		SelectedNoteItemId = null;
		SelectedRadioItemId = null;
	}
}
