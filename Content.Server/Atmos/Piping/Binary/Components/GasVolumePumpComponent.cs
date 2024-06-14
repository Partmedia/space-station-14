using Content.Shared.Atmos;

namespace Content.Server.Atmos.Piping.Binary.Components
{
    [RegisterComponent]
    public sealed partial class GasVolumePumpComponent : Component
    {
        [ViewVariables(VVAccess.ReadWrite)]
        [DataField("enabled")]
        public bool Enabled { get; set; } = true;

        [ViewVariables(VVAccess.ReadWrite)]
        [DataField("inlet")]
        public string InletName { get; set; } = "inlet";

        [ViewVariables(VVAccess.ReadWrite)]
        [DataField("outlet")]
        public string OutletName { get; set; } = "outlet";

        [ViewVariables(VVAccess.ReadWrite)]
        [DataField("transferRate")]
        public float TransferRate { get; set; } = Atmospherics.MaxTransferRate;

        [ViewVariables(VVAccess.ReadWrite)]
        [DataField("maxTransferRate")]
        public float MaxTransferRate { get; set; } = Atmospherics.MaxTransferRate;

        [DataField("lastMolesTransferred")]
        public float LastMolesTransferred;
    }
}
