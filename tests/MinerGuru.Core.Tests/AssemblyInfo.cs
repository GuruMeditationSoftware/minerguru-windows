using Xunit;

// The UI language (Lang.English, and with it Strings.Culture) is global for the process. Test classes set it in
// their constructor; running them side by side let one class switch the language under another's feet.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
