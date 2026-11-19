using Xunit;

// Env vars (HYPERV_FAKE_PS/HYPERV_ROOT) are process-global; serialize
// collections so ApiTests and EngineTests never interleave their setups.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
