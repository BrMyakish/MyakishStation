// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Radio;
using Content.Shared.StationRecords;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared.MedicalRecords.Components;

[RegisterComponent, NetworkedComponent]
public sealed partial class MedicalRecordsConsoleComponent : Component
{
    public const uint DefaultMaxNoteLength = 2048;

    [DataField]
    public uint? ActiveKey;

    [DataField]
    public StationRecordsFilter? Filter;

    [DataField]
    public uint MaxTitleLength = 96;

    [DataField]
    public uint MaxNoteLength = DefaultMaxNoteLength;

    [DataField]
    public ProtoId<RadioChannelPrototype> MedicalChannel = "Medical";
}
