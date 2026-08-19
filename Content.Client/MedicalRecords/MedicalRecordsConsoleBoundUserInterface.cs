// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Access.Systems;
using Content.Shared.MedicalRecords;
using Content.Shared.MedicalRecords.Components;
using Content.Shared.StationRecords;
using Robust.Client.Player;
using Robust.Shared.Prototypes;

namespace Content.Client.MedicalRecords;

public sealed class MedicalRecordsConsoleBoundUserInterface : BoundUserInterface
{
    [Dependency] private readonly IPlayerManager _players = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;

    private MedicalRecordsConsoleWindow? _window;

    public MedicalRecordsConsoleBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();

        var component = EntMan.GetComponent<MedicalRecordsConsoleComponent>(Owner);
        var access = EntMan.System<AccessReaderSystem>();
        _window = new MedicalRecordsConsoleWindow(
            Owner,
            component.MaxTitleLength,
            component.MaxNoteLength,
            _players,
            _prototypes,
            access);

        _window.OnKeySelected += key => SendMessage(new SelectStationRecord(key));
        _window.OnFiltersChanged += (type, value) => SendMessage(new SetStationRecordFilter(type, value));
        _window.OnCategoryFilterChanged += filter => SendMessage(new MedicalRecordSetCategoryFilterMessage(filter));
        _window.OnRecordNotesSaved += notes => SendMessage(new MedicalRecordSetNotesMessage(notes));
        _window.OnBodyDestroyedChanged += bodyDestroyed =>
            SendMessage(new MedicalRecordSetBodyDestroyedMessage(bodyDestroyed));
        _window.OnExaminationSaved += (id, title, note) =>
            SendMessage(new MedicalRecordUpdateExaminationMessage(id, title, note));
        _window.OnExaminationDeleted += id => SendMessage(new MedicalRecordDeleteExaminationMessage(id));
        _window.OnClose += Close;
        _window.OpenCentered();
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is MedicalRecordsConsoleState medicalState)
            _window?.UpdateState(medicalState);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _window?.Close();
    }
}
