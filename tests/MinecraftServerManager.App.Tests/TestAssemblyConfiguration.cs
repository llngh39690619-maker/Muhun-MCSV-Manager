using Xunit;

// WPF Application.Current and its ResourceDictionary are process-global. Keeping the App test
// assembly serial prevents headless tests from racing the single STA application's construction
// and also makes future WPF tests safe even if they forget to declare a custom collection.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
