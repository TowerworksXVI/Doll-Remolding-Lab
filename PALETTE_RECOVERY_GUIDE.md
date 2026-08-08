# Replacing skinned meshes in a game that pre-poses them — a porting guide

*This is the technique Doll Remolding Lab is built on — **skinning-palette recovery** — written so
it can be implemented on another game. Audience: a moderately technical modder or tools programmer.
Nothing here requires this app, this game, or any knowledge of either.*

## 1. The wall

Most mesh-replacement modding assumes the GPU receives a bind-pose mesh plus bone matrices, so an
interposer (3DMigoto or similar) can substitute new geometry and let the game skin it. Some games
instead skin on the CPU (or in an earlier compute pass): every draw the interposer sees receives
**already-posed vertices**, and no bone matrices are bound anywhere in the draw. On such a game
the community toolchain typically settles on two verbs and declares the rest impossible:

- **Hide** a part (skip its draw), and
- **Morph** it (push per-vertex offsets onto the posed vertices — same vertex count, no
  reweighting, no new geometry).

You cannot add a ponytail, swap in a different jacket, or change topology at all, because new
vertices have nothing to be posed by.

## 2. The key idea

Linear-blend skinning is **linear in the bone matrices**. For each posed vertex:

```
posed_v = Σ_k  w_{v,k} · ( [bind_v, 1] · M_{b_{v,k}} )
```

Hold the bind-pose mesh — rest positions, per-vertex weights, bone indices, all readable from the
game's asset files — and the posed buffer `q` the interposer intercepts satisfies `q = C·x`, where
`C` is built **only** from bind positions × weights (constant every frame) and `x` is the stacked
per-bone matrix rows: the per-frame palette, the thing the game consumed and never shipped.

So: precompute the pseudoinverse `C⁺` **offline, once per mesh**. Each frame, a small compute pass
computes `x = C⁺·q` from the live posed buffer — the palette is recovered. A second compute pass
then skins **your own geometry, any topology, any vertex count**, weighted to those same bones,
and a custom draw call renders it in place of the original (whose draw you skip). Reuse whatever
shaders the game itself bound at that draw and your mesh is lit and shadowed like a native part
(outlined too, if the game draws outlines as a per-part pass).

**Preconditions to verify on your game before building anything:**

- The game is true LBS (not dual-quaternion or blendshape-composited on the parts you target).
  Test: recover a palette from one intercepted frame, forward-skin the original bind mesh with it,
  and compare against the intercepted buffer. True LBS reproduces it at the float32 noise floor;
  anything structurally above that means a different skinning model.
- The intercepted posed buffer is **vertex-order-aligned** with the mesh in the asset files (the
  same test proves this).
- You can extract, per mesh: rest positions, blend weights/indices, bind poses, index buffer.
- Your interposer can run compute shaders, bind custom buffers, skip draws, and issue its own
  indexed draws. Stock 3DMigoto can do all of this from a mod folder.

## 3. The minimal pipeline

Offline, per replaced part: extract the bind mesh, build `C`, compute `C⁺`, ship it as a buffer
alongside your replacement's geometry and weights. At runtime, per frame:

1. **Capture** the posed vertex buffer at the part's draw (match the draw by a content-derived
   buffer hash — on our game every needed hash was computable offline from the asset files, so no
   frame captures were needed for authoring).
2. **Recover**: one compute dispatch, `x = C⁺·q`.
3. **Skin**: one compute dispatch, LBS of your geometry against `x`.
4. **Draw**: skip the original, issue your own `drawindexed` with the game's currently-bound
   shaders and the part's textures.

Run recover+skin **once per frame** (gate on a flag you reset at present), but let the draw fire at
**every** pass the original part drew in — that is what gives your mesh shadows and outlines for
free. Suppressing "extra" passes is a trap: it removes the mesh from the shadow map, not the noise.
The complement also holds: your mesh appears only where its host part draws, so a pass that part
never participates in (a reflection prepass, say) won't include the replacement either.

Two hygiene rules that cost us real debugging time: capture geometry **by reference** (posed
buffers upload before the frame's first draw, so a reference is always current-frame) but capture
per-draw constants **by copy at their own draw** (they're re-uploaded per draw; a reference aliases
whichever draw came last). And save/restore **by reference** every binding you touch around your
draws — setting a slot to null afterward is not a restore, it poisons every later draw that
expected the game's binding to persist.

## 4. The problem classes

Everything past the minimal pipeline exists because one of these problems is real on the game. They
are listed roughly in the order you will meet them.

### Your replacement uses bones its target part doesn't carry

*(a hair replacement that should reach the shoulders; a coat spanning torso bones the original
shirt never rigged)*

One part's mesh only constrains the bones it has weight on, so one recovery operator only sees a
slice of the skeleton. **Pool**: recover from several of the character's parts at once, each
covering its own bones, into one **union palette**. Give each bone a single owner — the pooled part
with the most summed weight on it — and have each part's recovery write **only the rows it owns**
(a scatter map with a "not mine" sentinel), so overlapping parts don't fight. Any rendered part can
serve; the union of a character's drawn parts covers every bone the animation meaningfully drives,
because a bone only produces visible motion by deforming something rendered.

### Pooled parts don't share an object space

*(the cape's object transform differs from the body's by a few degrees and a few centimeters)*

A recovered palette maps bind space → **that part's** posed object space. Renderers of one
character do not reliably share a transform, so rows recovered from part A are subtly wrong at
part B's draw — a slow lean or drift, not an obvious explosion. Rebase each owned row into the
anchor part's space: `row' = row · K` with `K = W_owner · W_anchor⁻¹`, the `W`s read from each
draw's object-to-world constants (captured by copy, per the hygiene rule above).

### At some draws, the constants can't be read

*(a preview ghost or minimap double of the character whose renderer binds constants as an offset
window into one shared buffer — a naive read returns the wrong window)*

When constant capture is unreliable, derive `K` from **geometry alone**: pick a **witness bone**
each pair shares (one both parts pose soundly), reserve palette slots for the two parts' separate
recoveries of it, and compute `K = M_witness_part⁻¹ · M_witness_anchor` in the convert pass. Same
rebase, no constants touched.

### Parts were authored in different bind spaces

*(the body's bind pose stands upright; the cloth's lies face-down; a prop's lies on its side)*

A union palette keeps one bind pose per bone, so every pooled part must be restated into one
space — pick the anchor's. The relation is a single rigid rotation when it's real; measure it
(from the game's own scene rest transforms when available, else fit it over bones the parts share
and require several corroborating bones), snap it to the exact axis-aligned rotation, and rebase
the part's bind poses and geometry with it. **Refuse anything that doesn't snap or isn't uniform
across shared bones** — bone-name identifiers collide across unrelated rigs, and "converting" on a
coincidence deforms the mesh silently.

### Some bones are numerically unrecoverable

*(a decimated distance-tier mesh leaves a bone 1–3% total weight on a handful of near-coplanar
vertices — its recovered rows are garbage and the limb visibly breaks)*

Inverting `C` is least-squares; a bone needs ≥4 well-spread weighted vertices to be determined.
Gate every bone at build time: pose a synthetic test palette, recover it **through the operator
rounded to the precision you actually ship** (float32 rounding is part of the product — a float64
check passes rows that ship broken), and mark bones whose error exceeds a threshold as weak. A weak
bone gets a **rigid tie**: its rows are replaced by its strongest co-riding sound bone's, so its
geometry rides that bone instead of exploding. When the weight involved is trace weight on both
sides, the tie is visually free — but a replacement that puts *real* weight on a bone your sources
only trace-weight will visibly ride the tie. Treat "the replacement leans on a bone no source
poses soundly" as a build-time refusal, not a silent tie.

### The operators are too big

*(4·bones × vertices floats per part — ~16 MB for one 12k-vertex, 80-bone part, times every pooled
part, times every detail tier)*

The full pseudoinverse row uses every vertex, but a bone's rows are determined by a small
well-chosen subset: its top-weighted vertices spread by farthest-point sampling (rank collapses if
you take them all from one seam), plus a few **discriminator** vertices weighted to its co-bones
but not to it (otherwise a bone proportionally co-weighted with a neighbour is locally
indistinguishable from it). Solve each bone locally over that subset and gate the result on its
**left-inverse defect** — how far the rows are from a true left inverse over the subset — not on a
single-pose residual, which a truncated solve can pass while being wrong for other poses. Escalate
the subset size for failing bones; a bone that never passes falls back to full width alone. Ours
came out ~35× smaller with every bone's defect within gate.

### Each part ships several detail tiers

*(the game swaps to a decimated mesh at distance, or renders it for previews — with its own buffer
hashes, so your overrides never fire on it)*

Buffer hashes are per-mesh, and a tier is a different mesh: cover every renderable tier of every
pooled part with its own capture/recover/draw chain, or the character pops back to vanilla exactly
when the game picks the tier you skipped. Two subtleties: a tier may **pose a bone that no pooled
top-detail part carries** (recruit a carrier — another part of the outfit whose top-detail mesh
poses that bone *and* which has a tier drawn alongside the asking tier, else the row is unwritten
exactly when it's read); and tiers are where ill-conditioned bones concentrate (see the
conditioning gate above).

### A recovery source can be off screen while your replacement draws

*(pooling an everywhere-part's replacement on a combat-only accessory, or on one option of an
outfit the player can switch)*

A palette segment is only as fresh as the last frame its source drew. A source that can be absent
while the target draws poses the target's bones from a stale buffer. **Presence rule**: a part may
feed the pool only if it is on screen **whenever the replaced part is**. Classify each part's
presence along whatever axes the game has (scene context; selectable outfit options) from game
data, and when the data can't classify a part, let it be replaced but never leaned on. The target
itself is always admissible — its capture fires exactly when the replacement is visible.

### A part outside the shadow pass draws nothing off camera

*(renderers authored with shadow casting Off: transparent overlays, and a scattering of opaque hair
and head parts)*

The shadow pass is what keeps a culled part's segment fresh (see the hard limit below), so a part
whose renderer opts out of it has no off-camera draw at all — frustum culling silences it
completely. **Shadow rule**: read the renderer's shadow-casting flag off the prefab and admit a
non-casting part to a pool, and to tier-bone carrier duty, only for its own replacement. The same
flag disqualifies it as a **presence witness**: a mesh that vanishes with the camera can't vouch
for what is worn. Its own replacement, retexture and hide are unaffected — each fires at that
part's own draw, which is exactly when its output matters. Make the exclusion ride a *measured*
Off, never an unread field.

### The game's own scene logic hides parts their names say are visible

*(components riding the model prefab that force nodes on or off per location, and per-clip node
lists on the idle and interaction scenes a character plays)*

A part's name and its wardrobe slot are not the whole story: shipped alongside the model there is
usually authored data that hides individual nodes in one location, dresses a garment on and off
independently of the scene, or flips a node mid-animation. A part under any of those draws on a
condition its name does not carry, so the presence rule above classifies it as visible and is
wrong. **Visibility rule**: read those overrides and treat every part they name the way an
unclassifiable part is treated — replaceable, but never leaned on for another part's pool, for
tier-bone carrier duty, or as a presence witness. Read only the lists that can *subtract* a draw;
one that only ever adds presence constrains nothing, and a serialized flag the game overwrites at
every apply says nothing at all. Match the way the game matches, including the entries it resolves
two ways and the ones it resolves neither way, so an override that does nothing in game demotes
nothing here either. And demote only on a list that named the part: a prefab you could not read
must leave every part admissible rather than none.

### Two different meshes share a match key

*(two outfit variants remodeled on the same index buffer; the same slot name reused with different
geometry)*

Content-derived hashes name **content**, and games reuse content across genuinely different
draw-time meshes. Have a ladder of discriminators: a second buffer whose content does differ; else
a runtime signal written into a persistent variable — recognizing a uniquely-tagged texture at the
draw, or **sibling meshes that only ever appear with one variant** vouching for it; else refuse the
replacement rather than let two meshes cross-fire one override. Assume from day one that "one hash
= one mesh" is false somewhere in the corpus, and key your build's sections on a signature that
survives that.

### Meshes that store fewer than four influences

*(one-influence rigid-worn props — glasses, badges, holsters, often index-only with implicit
weight 1 — and two-influence bodies and cloth)*

The stored influences are the mesh's whole skin: the draw is posed by exactly what the stream
carries, at any width. Verify that before assuming it — per-vertex weight sums come out at 1 when
the skin is complete, and visibly short if an exporter truncated influences away. Then widen
losslessly to the canonical 4-wide layout (pad zero-weight slots) and they pool like anything
else — with one caveat. Bone ownership (which part's captured draw a palette row is recovered
from) goes to the highest summed weight, and weight-1.0 on every vertex gives a ONE-influence
part an outsized sum for its size: measured on this corpus it wins ownership of roughly a third
of the bones it contests against real deformers. Whether that's harmful is a question about the
winner's draw reliability, not its weights — an always-drawn weight-1.0 rider is an exact read
of its bone, while a source whose draw can be absent leaves a stale row. This corpus keeps
one-influence parts out of other parts' pools as a conservative default; treat that as a policy
to justify against your game's draw behavior, not a law. A genuine multi-influence split
carries real fractions and pools freely.

### Meshes you must refuse (and say why)

- **Blendshape carriers**: morph deltas are added outside LBS, so recovery would absorb them as
  bone error. Refuse replacement; hide/retexture still work.
- **Parts posed by runtime physics** (spring/jiggle bone chains a simulation driver moves, not the
  animation system — they show up as dedicated bone-name families): the driver's parameters are
  authored against the original part, so this implementation refuses replacement rather than ship
  new geometry riding a simulation it wasn't tuned for; hide/retexture still work.
- **Skins spelled in a shape you can't read losslessly** (packed weight formats, a skin stream
  shared with unrelated channels): refuse rather than guess at a stride.
- **True statics** (no skin at all): no palette to recover — but none needed; a verbatim buffer
  swap is the whole job. Route them there instead.

Make the refusal rule **one predicate in one place**, and derive the routing, the build error, and
the UI gating from it — three hand-written copies of "can this mesh be replaced" will drift.

### Edits must survive a round trip through a DCC tool

*(Blender renames bones, reorders joints, appends `.001`, rewraps armatures)*

Positional joint identity dies in transit. Embed each bone's stable identity (the game's bone-name
hash) **in the exported joint's name**, and resolve everything coming back by that hash, never by
index or order. Influences painted to a bone the target doesn't carry: drop them, renormalize the
survivors, and warn with the bone names — tolerant by default, loud when the dropped weight was
real.

## 5. Hard limits

- **You cannot add bones.** The palette is recovered, not authored; articulation is capped by the
  vanilla rig. If the game's hand is one wrist bone, replacement fingers ride that one bone.
- A palette segment updates only when its source part draws. In practice this bites less than
  feared — verify on your game whether culled parts still draw as shadow casters (ours do, which
  keeps their segments fresh even fully off-camera). The exception is per-renderer: a part authored
  with shadow casting Off has no such draw, and the shadow rule above keeps it out.
- Precision is float32 end to end; every gate must measure what ships, not what a float64
  prototype does.

## 6. Order of attack

Climb a ladder, proving each rung in-game before the next: **hide** (skip the draw) →
**same-count vertex substitution** → **static in-engine skin** (compute-skinned bind mesh, fixed
palette) → **live recovery** (the true-LBS test *is* this rung) → **new topology via custom draw**
→ **pooled multi-part**. Every rung isolates one mechanism, so when a rung fails you know which
assumption about your game just broke. The problem classes of §4 then arrive one at a time, each
announced by a specific visual defect — and each has the shape of a rule you can gate at build
time, which is where you want every failure: a refused build with a reason beats a subtly leaning
character every time.
