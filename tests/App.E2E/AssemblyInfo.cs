// E2E tests each launch a real app and drive the single shared CI desktop via
// UI Automation — they must not run in parallel or they contend for focus,
// windows, and screenshots. Serialize the whole assembly.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
