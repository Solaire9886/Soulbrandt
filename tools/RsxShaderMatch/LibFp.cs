namespace RsxShaderMatch;

/// <summary>One decoded library fragment program: its dev name, parsed name features, and microcode fingerprint.</summary>
sealed record LibFp(string Name, LibName Features, RsxFp.Fingerprint Fp, int HeaderInstrCount);
