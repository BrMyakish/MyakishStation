// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.MedicalRecords;

namespace Content.Client.MedicalRecords;

public sealed class MedicalNotesBoundUserInterface : BoundUserInterface
{
    private MedicalNotesWindow? _window;

    public MedicalNotesBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();

        _window = new MedicalNotesWindow();
        _window.OnNotesSaved += notes => SendMessage(new MedicalNotesSetMessage(notes));
        _window.OnClose += Close;
        _window.OpenCentered();
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (state is MedicalNotesState medicalState)
            _window?.UpdateState(medicalState);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _window?.Close();
    }
}
