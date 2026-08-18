// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.StationRecords;
using Robust.Shared.GameStates;

namespace Content.Shared.MedicalRecords.Components;

[RegisterComponent, NetworkedComponent]
public sealed partial class MedicalRecordsConsoleComponent : Component
{
    [DataField]
    public uint? ActiveKey;

    [DataField]
    public StationRecordsFilter? Filter;

    [DataField]
    public uint MaxTitleLength = 96;

    [DataField]
    public uint MaxNoteLength = 2048;
}
