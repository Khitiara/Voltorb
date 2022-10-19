namespace Voltorb.Vorbis;

/// <summary>
/// A reader capable of fast decode of Vorbis-I encoded audio
/// 
/// Vorbis I encoding assumes a single non-interleaved logical bitstream, and as such the normal de-interleaving
/// process may be elided and seeking can be assumed to be possible without interfering with other interleaved
/// data streams.
/// </summary>
public class VorbisIReader
{ }