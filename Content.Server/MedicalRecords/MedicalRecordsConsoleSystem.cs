// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server.Popups;
using Content.Server.Station.Systems;
using Content.Server.StationRecords.Components;
using Content.Server.StationRecords.Systems;
using Content.Shared.Access.Systems;
using Content.Shared.MedicalRecords;
using Content.Shared.MedicalRecords.Components;
using Content.Shared.Popups;
using Content.Shared.StationRecords;
using Robust.Server.GameObjects;

namespace Content.Server.MedicalRecords;

/// <summary>
/// Medical records are public to read, while every mutation is validated against Medical access.
/// </summary>
public sealed class MedicalRecordsConsoleSystem : EntitySystem
{
    [Dependency] private readonly AccessReaderSystem _access = default!;
    [Dependency] private readonly MedicalRecordsSystem _medicalRecords = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly StationRecordsSystem _records = default!;
    [Dependency] private readonly StationSystem _station = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<MedicalRecordsConsoleComponent, RecordModifiedEvent>(OnRecordChanged);
        SubscribeLocalEvent<MedicalRecordsConsoleComponent, AfterGeneralRecordCreatedEvent>(OnRecordChanged);

        Subs.BuiEvents<MedicalRecordsConsoleComponent>(MedicalRecordsConsoleKey.Key, subs =>
        {
            subs.Event<BoundUIOpenedEvent>(OnUiOpened);
            subs.Event<SelectStationRecord>(OnKeySelected);
            subs.Event<SetStationRecordFilter>(OnFilterChanged);
            subs.Event<MedicalRecordSetNotesMessage>(OnSetNotes);
            subs.Event<MedicalRecordUpdateExaminationMessage>(OnUpdateExamination);
            subs.Event<MedicalRecordDeleteExaminationMessage>(OnDeleteExamination);
        });
    }

    private void OnRecordChanged<T>(Entity<MedicalRecordsConsoleComponent> ent, ref T args)
    {
        UpdateUi(ent);
    }

    private void OnUiOpened(Entity<MedicalRecordsConsoleComponent> ent, ref BoundUIOpenedEvent args)
    {
        UpdateUi(ent);
    }

    private void OnKeySelected(Entity<MedicalRecordsConsoleComponent> ent, ref SelectStationRecord msg)
    {
        ent.Comp.ActiveKey = msg.SelectedKey;
        UpdateUi(ent);
    }

    private void OnFilterChanged(Entity<MedicalRecordsConsoleComponent> ent, ref SetStationRecordFilter msg)
    {
        if (msg.Type is StationRecordFilterType.DNA or StationRecordFilterType.Species)
            return;

        if (ent.Comp.Filter?.Type == msg.Type && ent.Comp.Filter.Value == msg.Value)
            return;

        ent.Comp.Filter = new StationRecordsFilter(msg.Type, msg.Value);
        UpdateUi(ent);
    }

    private void OnSetNotes(Entity<MedicalRecordsConsoleComponent> ent, ref MedicalRecordSetNotesMessage msg)
    {
        if (!TryGetEditableKey(ent, msg.Actor, out var key))
            return;

        var notes = msg.Notes.Trim();
        if (notes.Length > ent.Comp.MaxNoteLength)
            return;

        _medicalRecords.TrySetNotes(key, notes);
    }

    private void OnUpdateExamination(Entity<MedicalRecordsConsoleComponent> ent, ref MedicalRecordUpdateExaminationMessage msg)
    {
        if (!TryGetEditableKey(ent, msg.Actor, out var key))
            return;

        var title = msg.Title.Trim();
        var note = msg.Note.Trim();
        if (title.Length < 1 || title.Length > ent.Comp.MaxTitleLength || note.Length > ent.Comp.MaxNoteLength)
            return;

        _medicalRecords.TryUpdateExamination(key, msg.Id, title, note);
    }

    private void OnDeleteExamination(Entity<MedicalRecordsConsoleComponent> ent, ref MedicalRecordDeleteExaminationMessage msg)
    {
        if (!TryGetEditableKey(ent, msg.Actor, out var key))
            return;

        _medicalRecords.TryDeleteExamination(key, msg.Id);
    }

    private bool TryGetEditableKey(
        Entity<MedicalRecordsConsoleComponent> ent,
        EntityUid user,
        out StationRecordKey key)
    {
        key = default;

        if (!_access.IsAllowed(user, ent))
        {
            _popup.PopupEntity(Loc.GetString("medical-records-permission-denied"), ent, user, PopupType.MediumCaution);
            return false;
        }

        if (ent.Comp.ActiveKey is not { } id || _station.GetOwningStation(ent) is not { } station)
            return false;

        key = new StationRecordKey(id, station);
        return true;
    }

    private void UpdateUi(Entity<MedicalRecordsConsoleComponent> ent)
    {
        var station = _station.GetOwningStation(ent);
        if (!TryComp<StationRecordsComponent>(station, out var stationRecords))
        {
            _ui.SetUiState(ent.Owner, MedicalRecordsConsoleKey.Key, new MedicalRecordsConsoleState());
            return;
        }

        var listing = _records.BuildListing((station.Value, stationRecords), ent.Comp.Filter);
        if (ent.Comp.ActiveKey is { } active && !listing.ContainsKey(active))
            ent.Comp.ActiveKey = null;

        MedicalRecordProfile? profile = null;
        MedicalRecord? medicalRecord = null;

        if (ent.Comp.ActiveKey is { } id)
        {
            var key = new StationRecordKey(id, station.Value);
            if (_records.TryGetRecord<GeneralStationRecord>(key, out var general, stationRecords))
                profile = new MedicalRecordProfile(general.Name, general.JobTitle, general.Species, general.Age);

            _records.TryGetRecord(key, out medicalRecord, stationRecords);
        }

        var state = new MedicalRecordsConsoleState(
            ent.Comp.ActiveKey,
            profile,
            medicalRecord,
            listing,
            ent.Comp.Filter);

        _ui.SetUiState(ent.Owner, MedicalRecordsConsoleKey.Key, state);
    }
}
