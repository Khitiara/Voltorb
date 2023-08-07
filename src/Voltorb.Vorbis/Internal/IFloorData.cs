namespace Voltorb.Vorbis.Internal;

internal interface IFloorData
{
    bool ExecuteChannel { get; }
    bool ForceEnergy { get; set; }
    bool ForceNoEnergy { get; set; }
}