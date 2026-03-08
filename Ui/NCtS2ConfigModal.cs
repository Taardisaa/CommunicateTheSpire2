using Godot;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;

namespace CommunicateTheSpire2.Ui;

/// <summary>
/// In-game config modal for CommunicateTheSpire2. Built in C# (no scene).
/// </summary>
public partial class NCtS2ConfigModal : Control, IScreenContext
{
	private CheckButton _enabledCheck = null!;
	private CheckButton _verboseLogsCheck = null!;
	private LineEdit _commandEdit = null!;
	private LineEdit _workingDirEdit = null!;
	private Control? _defaultFocus;

	public Control? DefaultFocusedControl => _defaultFocus;

	public override void _Ready()
	{
		var cfg = Config.CommunicateTheSpire2Config.LoadOrCreateDefault();
		var panel = new PanelContainer
		{
			AnchorRight = 1,
			AnchorBottom = 1,
			OffsetLeft = 200,
			OffsetTop = 150,
			OffsetRight = -200,
			OffsetBottom = -150
		};
		var style = new StyleBoxFlat
		{
			BgColor = new Color(0.1f, 0.1f, 0.12f, 0.95f),
			BorderWidthLeft = 2,
			BorderWidthTop = 2,
			BorderWidthRight = 2,
			BorderWidthBottom = 2,
			BorderColor = new Color(0.4f, 0.35f, 0.3f)
		};
		panel.AddThemeStyleboxOverride("panel", style);

		var vbox = new VBoxContainer
		{
			LayoutMode = 1,
			AnchorRight = 1,
			AnchorBottom = 1,
			OffsetLeft = 20,
			OffsetTop = 20,
			OffsetRight = -20,
			OffsetBottom = -20
		};
		vbox.AddThemeConstantOverride("separation", 12);
		panel.AddChild(vbox);

		var title = new Label { Text = "CommunicateTheSpire2" };
		title.AddThemeFontSizeOverride("font_size", 20);
		vbox.AddChild(title);

		_enabledCheck = new CheckButton { Text = "Enable controller", ButtonPressed = cfg.Enabled };
		vbox.AddChild(_enabledCheck);

		_verboseLogsCheck = new CheckButton { Text = "Verbose protocol logs (state summary, checksum)", ButtonPressed = cfg.VerboseProtocolLogs };
		vbox.AddChild(_verboseLogsCheck);

		var cmdLabel = new Label { Text = "Controller command (e.g. python -u controller/simple_policy_controller.py)" };
		vbox.AddChild(cmdLabel);
		_commandEdit = new LineEdit
		{
			Text = cfg.Command ?? "",
			PlaceholderText = "python -u path/to/controller.py",
			CustomMinimumSize = new Vector2(400, 0)
		};
		vbox.AddChild(_commandEdit);

		var dirLabel = new Label { Text = "Working directory (optional)" };
		vbox.AddChild(dirLabel);
		_workingDirEdit = new LineEdit
		{
			Text = cfg.WorkingDirectory ?? "",
			PlaceholderText = "C:\\path\\to\\CommunicateTheSpire2",
			CustomMinimumSize = new Vector2(400, 0)
		};
		vbox.AddChild(_workingDirEdit);

		var btnRow = new HBoxContainer();
		btnRow.AddThemeConstantOverride("separation", 16);
		vbox.AddChild(btnRow);

		var saveBtn = new Button { Text = "Save" };
		saveBtn.Pressed += OnSavePressed;
		btnRow.AddChild(saveBtn);

		var cancelBtn = new Button { Text = "Cancel" };
		cancelBtn.Pressed += OnCancelPressed;
		btnRow.AddChild(cancelBtn);

		AddChild(panel);
		_defaultFocus = _commandEdit;
	}

	private void OnSavePressed()
	{
		var cfg = Config.CommunicateTheSpire2Config.LoadOrCreateDefault();
		cfg.Enabled = _enabledCheck.ButtonPressed;
		cfg.VerboseProtocolLogs = _verboseLogsCheck.ButtonPressed;
		cfg.Command = _commandEdit.Text?.Trim() ?? "";
		cfg.WorkingDirectory = string.IsNullOrWhiteSpace(_workingDirEdit.Text) ? null : _workingDirEdit.Text.Trim();
		Config.CommunicateTheSpire2Config.TryWrite(cfg);
		ModEntry.ApplyConfigFromFile();
		NModalContainer.Instance?.Clear();
	}

	private void OnCancelPressed()
	{
		NModalContainer.Instance?.Clear();
	}
}
