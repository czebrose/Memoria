# Mod compatibility checks

The Black Waltz mod ([Vuux2/ff9-black-waltz-mod](https://github.com/Vuux2/ff9-black-waltz-mod), private) ships C#
scripts that the game loads, already compiled, as `Memoria.Scripts.HadesWorkshopMod.dll`. They use this build in
two ways, and each has a check:

| Check | What it guards | If it breaks |
|---|---|---|
| **References** (`Test-ModReferences.ps1`) | What the mod's compiled code calls or reads directly: about 400 types, methods, fields and constructors, by exact signature. Examples: `EventEngine.GetControlChar()`, `BattleHUD.SetBattleMessage(String, Byte)`, `ff9item._FF9Item_Data`. | The game throws `MissingMethodException`, `MissingFieldException` or `TypeLoadException` when it runs that script. |
| **Lookups** (`Test-ModLookups.ps1`) | Names the mod looks up as text, through reflection, mostly private: `SessionLobby._hostPlays`, `GhostActor._animClock`, `BattleHUD._partyDetail`. The compiler doesn't see these. | The lookup returns null, and the mod's code that relies on it quietly stops working. |

`tools\multiplayer\build.ps1` runs both after every build:

```
Mod references: 407 checked against ...\Output (list from Vuux2/ff9-black-waltz-mod @ bf6347dc...)
Mod references: all found
Mod lookups: 148 checked against ...\Output\Assembly-CSharp.dll (list from Vuux2/ff9-black-waltz-mod @ bf6347dc...)
  40 skipped: for the local multiplayer build (mod-lookups-other-build.txt)
Mod lookups: all found
```

If anything is missing or changed, the build fails and lists it, with the mod code that uses it:

```
  FAIL  method BattleHUD.SetBattleMessage(System.String,System.Byte) not found; it now takes (System.String,System.Byte,System.Boolean)   (used by BlackWaltzBanter+<Talk>d__0)
  FAIL  Memoria.Online.Session.SessionLobby field _hostPlays: ... has no field _hostPlays   (used by OnlineWatchers.cs)
```

The first line is a case the compiler can't catch: adding an optional parameter still compiles here, but the mod's
already-compiled DLL calls the old signature. To fix a failure, either keep the old name or signature (for example
add an overload instead), or agree the change with the mod so its side changes too. Then update the lists as
described below. `build.ps1 -SkipModCheck` builds without the checks.

The checks need only this folder and the build. They don't need the mod or GitHub.

## Files

| File | What it is |
|---|---|
| `mod-references.txt` | Everything the mod's compiled scripts use of this build (Assembly-CSharp, Memoria.Prime), by signature, with the mod classes that use each one. Generated. |
| `mod-lookups.txt` | Every type and member the mod's scripts look up by name, with the mod files that do it. Generated. |
| `mod-lookups-types.txt` | Hand-kept. The type for the few lookups whose object the scan can't trace, such as a method parameter or `UIManager.Battle`. |
| `mod-lookups-other-build.txt` | Hand-kept. Lookups for the local (same-PC) multiplayer build. The mod does without them on this build, so they're skipped. |
| `Update-ModReferences.ps1`, `Update-ModLookups.ps1` | Rebuild the two generated lists from the mod. |
| `Test-ModReferences.ps1`, `Test-ModLookups.ps1` | Check the lists against a build. `build.ps1` runs them; they also run alone. |
| `ModReferences.cs` | The DLL reader both reference scripts use. |

Both generated lists record the mod commit they came from in their `# Source:` line. Don't edit them by hand.

## Updating the lists when the mod changes

Update the lists when the mod gets new or changed C# scripts (`src/memoria/StreamingAssets/Scripts/`). A release is
a good point to do it, or when the mod's team hands off a change that relies on the build. Build first, so `Output\`
holds the current build: the reference list is read against it.

```powershell
cd tools\multiplayer\mod-check
.\Update-ModReferences.ps1                # the mod's main branch on GitHub (gh CLI, signed in with access)
.\Update-ModLookups.ps1
.\Test-ModReferences.ps1                  # check both against the current build
.\Test-ModLookups.ps1
git diff .                                # what was added or removed, and the mod commit
```

Each `Update-` script also takes `-Ref <commit>` for another mod commit, or `-ModPath <dir>` to read a local checkout
of the mod instead of GitHub. Give both scripts the same one. Commit the two lists together with any changes to the
hand-kept files.

**If `Update-ModReferences.ps1` stops** with "The mod's DLL uses things the build in Output\ doesn't have":
- If the mod was built against a newer build than yours, build that first.
- Otherwise this build broke them. Run `Test-ModReferences.ps1` with the old list to see what changed.

**If `Update-ModLookups.ps1` stops** with "These lookups don't say which type they're on":
1. Open the mod file and line it names, and find which build type the object is.
2. Add a line `<file> <name> <type>` to `mod-lookups-types.txt`. Use `-` if the type isn't from Assembly-CSharp, for
   example a `HashSet`'s `Clear`.
3. Run the update again.

**If the lookup check fails on something the mod only uses on the local multiplayer build**, add it to
`mod-lookups-other-build.txt`. Only do that after checking it exists on that build's `multiplayer` branch.

## How the reference list is made

`Update-ModReferences.ps1` loads the mod's DLL next to the current build and reads what it uses:
- every instruction in its methods that names a type, method or field (calls, field reads and writes, `new`, casts,
  `typeof`);
- the types in its own signatures, local variables and base types;
- every method of our interfaces it implements, and our methods it overrides.

Members of generic types are recorded on the generic definition, with generic parameters by position:
`PersistenSingleton`1.get_Instance() : !0`.

The check then finds each one in a build with the same parameter types, return type, static or instance, and field
type. Anything that was public must still be public.

## How the lookup scan works

`Update-ModLookups.ps1` reads each script's source and records every `GetType("…")`,
`GetField/GetProperty/GetMethod("…")`, and every call to the mod's `GetMember(obj, "…")` / `Get(obj, "…")`
helpers. For each one, it works out which type it's on by following the variables involved:

- `Type t = game.GetType("X")` or `typeof(X)`: X.
- `t.GetNestedType("N")`: X+N.
- `fi.FieldType`, an object from `fi.GetValue(…)`, or `obj.GetType()`: the type of field `fi` (written `X::field`).
- An item of a dictionary or list, from `dict[key]` or `foreach`: written `X::field[]`.

The checker works out `::` and `[]` from the build itself. A property and a field with the same name looked up
within a few lines count as alternatives, as in `GetProperty("x") ?? GetField("x")`.

## Limits

- **They check that things exist, not what they do.** A method that keeps its signature but behaves differently
  passes. A looked-up field that changes type also passes the lookup check.
- **Behaviour still needs other tests or a two-PC run.** Examples are script dictionary 9900 keys, message numbers,
  and who gets which character.
- **Interfaces:** adding a method to an interface the mod implements isn't caught.
