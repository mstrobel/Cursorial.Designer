# Proposal: PreviewHost assembly resolution

**Status:** Proposed — pinned, not scheduled.
**Scope:** `Cursorial.Designer.PreviewHost`, `plugin/` diagnostics.

## Problem

The preview host and the user's project each carry their own copy of every `Cursorial.*`
assembly. The host gets its copies from `ProjectReference`s into `$(CursorialRepoRoot)`, copied
into `Cursorial.Designer.PreviewHost/bin/Debug/net10.0/` at *host build time*. The user's project
gets its own copies into its output directory at *their build time*.

`PreviewSession.cs:568` then loads the user's assembly with:

```csharp
XamlSchemaContext.Default.RegisterAssembly(Assembly.LoadFrom(path));
```

`Assembly.LoadFrom` probes the **target's** directory for that assembly's dependencies. So the
process ends up with two candidate files for each `Cursorial.*` identity: the host's copy, already
loaded, and the target's copy sitting next to the assembly being loaded. `LoadFrom` unifies them by
assembly identity, which silently papers over the duplication *as long as the two builds agree*.

When they disagree, the load fails with `FileLoadException` on whichever assembly drifted.

### Why this is a routine occurrence, not an edge case

Editing the framework refreshes the framework's own output and the user's app on their next build,
but it never refreshes the host's copies — only rebuilding the PreviewHost does that. So any
framework change followed by an app rebuild puts the two sides at different vintages. Observed
directly on 2026-08-06, mid-session:

| assembly | host copy | user project copy |
| --- | --- | --- |
| `Cursorial.Core` | matches | matches |
| `Cursorial.Rendering` | matches | matches |
| `Cursorial.Drawing` | matches | matches |
| `Cursorial.UI` | **differs** | **differs** |

One divergent assembly is enough. This is also why the failure looked intermittent for weeks: it
only bites when `Cursorial.UI` (or any other framework assembly) changes *between* a host build and
an app build. It stays invisible while both happen to move together.

The workaround in use today — rebuild the PreviewHost so its copies are refreshed — works, but it
makes a framework edit cost a host rebuild, and the requirement is invisible until it fails.

## Desired behaviour

> The preview should run against whichever framework version the user is working against, so long
> as it is at least as new as the version the host was built against.

This is the right rule, not merely a convenient one: the previewer exists to show what the user's
app will actually do, so the user's framework build is the authoritative one. The host's bundled
copies are a floor, not a ceiling.

## Why the obvious fix does not work

The natural first move is to isolate the target in an `AssemblyLoadContext` with an
`AssemblyDependencyResolver`. That fails for this codebase, in either direction:

- **Resolving `Cursorial.*` to the host's copies** inverts the requirement above — the user's
  newer framework would be ignored, and the preview would show stale behaviour.
- **Resolving `Cursorial.*` to the target's copies**, while leaving the host as-is, is worse than
  the status quo. The host binds the framework into the *default* load context before it ever
  reaches the target: `PreviewSession.cs:105-106` registers `typeof(Toolbar).Assembly` and
  `typeof(Cursorial.UI.Dialogs.MessageBox).Assembly`, and the host's own logic sits on
  `UIHeadlessHost`, `XamlLoader` and the render tree. Loading a second `Cursorial.UI` into a child
  context yields two genuinely non-unified copies, trading today's clean `FileLoadException` for
  `InvalidCastException`s across the context boundary — a strictly harder failure to diagnose.

The constraint is therefore structural: **whatever loads the framework must also load the host
logic**, so that exactly one copy of `Cursorial.*` exists and both sides bind to it.

## Proposed design

Split the host into a launcher and a core, and give the core an isolated context whose framework
copies come preferentially from the user's project.

### Project layout

- `Cursorial.Designer.PreviewHost` — thin entry point, **zero** `Cursorial.*` references. Owns
  process startup, the stdio pipe, and load-context construction. Its only job is to decide where
  the framework comes from and hand off.
- `Cursorial.Designer.PreviewHost.Core` — everything that exists today, unchanged, still
  `ProjectReference`-ing the framework. Loaded *into* the context the launcher creates.

`Cursorial.Designer.Protocol` stays where it is; it has no framework dependency and can remain in
the default context so the launcher can report failures before the core is loadable.

### Resolution

On session start, given the user's assembly path, the launcher creates one `AssemblyLoadContext`
with a resolver that, for `Cursorial.*`:

1. Probes the **user project's output directory** first.
2. Falls back to the host's bundled copies.

The core, the framework, and the user's assembly all load into that single context. One copy of
everything, sourced preferentially from the user's build.

### Version gate

Before preferring the target's copies, compare the target's `Cursorial.Core` assembly version
against the host's bundled one:

- target **≥** bundled → use the target's framework (the normal path).
- target **<** bundled → fall back to the bundled framework and emit a diagnostic through the
  protocol, e.g. *"Previewing against the designer's Cursorial `<bundled>`; this project targets
  the older `<target>`. Some behaviour may differ."*

Silent fallback is the thing to avoid — the current failure mode is confusing precisely because it
is invisible until it throws.

Note that "at least as new" is necessary but not sufficient in general: a newer framework can still
break a host that uses an API which changed shape. Within a repo where both move together this is
acceptable, provided load failures are reported clearly rather than surfacing as a dead process.

## Consequences

- A framework edit no longer requires a PreviewHost rebuild. Edit the framework, build the app,
  and the preview reflects it.
- The two-copies failure becomes unexpressible: there is exactly one source for `Cursorial.*` in
  any given session.
- The bundled `dotnet/` payload reverts to being a genuine fallback — for users without a source
  checkout — rather than a shadow copy that must be kept in sync.

## Risks and things to verify

- **`XamlSchemaContext.Default` is a static.** It lives in whichever context loads
  `Cursorial.UI.Xaml` — under this design, the child. Verify nothing in the thin launcher touches
  it, or it will initialise in the wrong context.
- **UI-thread affinity is unaffected.** `UIHeadlessHost.Create` makes the *calling thread* the UI
  thread; that contract keys off the thread, not the load context. Worth confirming under test
  rather than assuming.
- **Resolver coverage.** The resolver must handle the framework's full dependency graph, not just
  the assemblies named directly by the user's project.
- **Unloadability is not a goal.** A collectible context is not required; sessions can be
  process-scoped as they are today. Not pursuing collectibility avoids a class of teardown bugs.

## Related diagnostics improvement

`plugin/src/main/kotlin/dev/cursorial/designer/previewer/PreviewHostProcess.kt:224` logs
non-JSON stdout as `"Dropping malformed event from preview host"`. Because stdout *is* the protocol
channel, a host that fails to launch reports itself as a protocol parse error. Any non-JSON output
seen before the first valid protocol message should be treated as launch failure and surfaced with
its text intact — this is what would have made the `FileLoadException` above self-evident.

## Interim workaround

Until this lands, with `CURSORIAL_PREVIEWHOST_DLL` pointing at the dev host, the sandbox and Gradle
are out of the picture. Refreshing the host's framework copies is then:

```sh
dotnet build Cursorial.Designer.PreviewHost/Cursorial.Designer.PreviewHost.csproj   # ~21s
```

followed by the **Restart** action in the preview editor toolbar
(`plugin/src/main/kotlin/dev/cursorial/designer/editor/CursorialPreviewEditor.kt:632`).

Caveat, and it is not a small one: this build follows `ProjectReference` into
`$(CursorialRepoRoot)` and therefore *writes into the main Cursorial checkout*, building only the
host's reference closure. That leaves assemblies outside the closure at an older vintage than the
ones inside it — the `TypeLoadException`-on-one-specific-type failure. Only run it against a clean,
compiling tree, and prefer a whole-solution build if anything else in the checkout is in flight.

## Testing

- Round-trip test: build a fixture project against a framework newer than the host's bundled copy;
  assert the session loads and reports the target's version.
- Negative test: fixture built against an *older* framework; assert fallback plus the diagnostic.
- Regression test for the original failure: host and target at deliberately divergent builds of the
  same version; assert no `FileLoadException`.
