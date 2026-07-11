# Draft: JetBrains feature request (YouTrack, RIDER project)

Paste-ready draft below the rule. Suggested type: Feature. Suggested subsystem: XAML.

---

**Title:** Extension point for third-party XAML dialects (custom UI frameworks) in Rider's XAML support

**Environment:** Rider 2026.1; a custom .NET UI framework whose markup is XAML 2006/2009-compliant

## Summary

Rider's XAML engine recognizes a fixed set of frameworks (WPF, Avalonia, MAUI, Xamarin.Forms, WinUI/UWP). XAML documents from any other framework — even fully XAML 2006/2009-compliant markup — are analyzed against no schema: every element and attribute is flagged as unresolved, and there is no supported way for a plugin to either teach the engine a new dialect or ask it to stand down for files it doesn't understand.

## Use case

I maintain Cursorial, a .NET terminal UI framework whose markup is standard XAML 2006/2009: the standard `x:` directives, `XmlnsDefinition`-style URI→CLR-namespace mapping declared by assembly attributes, markup extensions, attached properties. I ship a Rider plugin (https://github.com/mstrobel/Cursorial.Designer) providing a live visual designer for these documents. Editing is where the wall is:

- The built-in XAML analysis red-flags every token of a Cursorial document. The only mitigation is keeping the files under a build action the XAML engine ignores.
- On the frontend, `.xaml` belongs to Rider's own file type and exposes no XML-language PSI, so even generic XML editing features (tag matching, auto-closing, typed handlers) don't apply to these files, and language-keyed plugin extensions never fire — a plugin must register extensions with `language="any"` and gate per file.
- As a result the plugin rebuilds the entire editing stack in parallel: an out-of-process language service plus external annotator, completion contributor, and typed handlers. This works, but it duplicates machinery Rider's XAML engine already has in generic form, and the semantic features that require the ReSharper backend — find usages of a C# symbol in XAML, rename across XAML and C# — are structurally out of reach for a frontend-only plugin.

## Request

In increasing order of ambition; the first tier alone would already remove the sharpest pain:

1. **Stand-down/claim API.** Let a plugin declare ownership of specific XAML documents (e.g. by root xmlns URI), suppressing built-in XAML inspections for those files while keeping generic XML editing behavior available to them.
2. **Dialect registration.** An extension point to register a XAML framework: its root/default xmlns URIs, how URI→CLR-namespace mapping is declared in referenced assemblies (the attribute's FQN), and its base-element/attached-property conventions — so the existing engine can provide resolution, completion, and navigation for the dialect generically.
3. **Backend semantic integration.** For registered dialects, participation in find-usages and rename between XAML references and C# symbols.

## Current behavior

No extension point exists; dialect detection appears keyed to known framework assemblies and hard-coded `XmlnsDefinitionAttribute` type names, with no fallback for compliant-but-unknown XAML.

## Expected behavior

Third-party XAML-based frameworks can integrate with Rider's XAML support the way third-party languages integrate with the IntelliJ platform — declaratively, from a plugin, without masquerading as WPF or Avalonia.
