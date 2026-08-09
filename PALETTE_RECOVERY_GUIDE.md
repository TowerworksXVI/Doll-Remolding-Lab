# Replacing meshes that the game pre-skins

*A game-agnostic porting guide to recovering and reusing a live bone palette.*

This document describes a runtime mesh-replacement technique for games that submit already-posed
skinned vertices instead of exposing their bone palette to an interposer such as 3DMigoto. It is a
portable technique, but no implementation detail should be assumed portable without measurement.

To keep that boundary visible, the guide uses three labels:

- **Principle** — a mathematical or API-independent requirement.
- **Measure** — an engine, game, or renderer behavior that must be established from captures.
- **Policy** — a conservative implementation choice. Another implementation may support more.

The examples come from one production implementation, but the rules are written so another game can
be evaluated without inheriting its assumptions.

## 1. The wall

Some games perform linear-blend skinning before the ordinary draw. The vertex shader receives a
buffer whose positions, normals, and tangents are already posed. The bone matrices that produced it
may have existed only in an earlier compute pass or in engine-private buffers that a draw interposer
cannot address.

A conventional mesh override can replace those submitted vertices, but it cannot change their count
or topology and still obtain animation. A same-count substitution can move the existing vertices,
but new vertices have no posed positions.

The useful observation is that the submitted positions contain enough linear evidence to recover
some or all of the palette that posed them.

## 2. The recovery model

### 2.1 Linear-blend skinning as a linear system

Assume row-vector notation. For bind-position p at vertex v:

    posed_v = Σ_k w_v,k · ([p_v, 1] · M_b(v,k))

For N vertices and B bones, collect the posed xyz positions into q, the unknown affine palette rows
into x, and construct C from the bind positions, bone indices, and weights:

    q = C · x

For every nonzero influence of bone b at vertex v, the four columns belonging to b receive:

    w_v,b · [p_v.x, p_v.y, p_v.z, 1]

C is constant while the mesh's bind geometry and skin are constant. A recovery operator can
therefore be computed offline:

    x̂ = C⁺ · q

At runtime, one compute pass applies that operator to the live posed positions. A second pass skins
replacement geometry against the recovered palette.

**Principle:** The recovered matrix is the complete bind-to-posed affine map used by the submitted
positions. Matrix layout and multiplication order may be transposed in a column-vector engine, but
the model is the same.

### 2.2 Compatibility is not identifiability

Two tests answer different questions and must not be conflated.

The forward residual:

    r_forward = C · (C⁺ · q) - q

tests whether the captured positions are representable by the proposed LBS system. A small residual
supports the hypotheses that the vertex order, bind data, skin data, and posing model are compatible.
It does not prove any one of those hypotheses by itself, and a large residual does not identify which
one failed.

The left-inverse defect:

    D = C⁺ · C - I

tests whether the palette rows are identifiable. If C has a null space, multiple palettes can
reproduce the original mesh exactly. The minimum-norm answer may still pose new geometry incorrectly
because the new geometry has different positions or weights.

**Principle:** A low source-mesh replay residual is necessary evidence for this technique, not proof
that every recovered bone is usable by a replacement.

**Measure:** Test several materially different animation poses. One pose can accidentally hide a
wrong vertex order, a truncated operator, or an unobservable bone.

**Measure:** Test the operator in the representation that will ship. Quantization of the source
buffer, float32 operator coefficients, packed formats, and compute arithmetic are all part of the
result.

### 2.3 Preconditions

Establish all of the following before building a full replacement pipeline:

- The target draw is posed by LBS, or by a model that can be reduced to the same fixed linear system.
- The intercepted posed positions are aligned to the asset mesh's vertex order.
- Rest positions, bone identities, indices, weights, and bind poses can be read without guessing.
- The interposer can bind custom resources, dispatch compute, suppress the original draw, and issue
  a replacement indexed draw.
- Every bone used materially by the replacement is recoverable from at least one admissible source,
  or the build refuses it.
- The replacement's vertex streams satisfy the shaders' actual input contract.
- The source draw occurs in every scene and pass in which the replacement must appear, or another
  host and routing strategy is supplied.

Blend shapes, cloth deformation, vertex animation, procedural offsets, and post-skin effects do not
automatically make the method impossible, but they invalidate the fixed C model unless their state
and ordering are incorporated.

## 3. The minimal pipeline

Offline, for one replaced part:

1. Extract the bind mesh and skin.
2. Build and condition the recovery operator.
3. Compile the replacement geometry and weights into the recovered palette's bone order.
4. Ship the operator, bind vertices, replacement skin, index buffer, and required material streams.

At runtime:

1. **Capture** the live posed source positions.
2. **Recover** the palette from those positions.
3. **Convert** the recovered rows into the replacement host's object space when necessary.
4. **Skin** the replacement geometry.
5. **Draw** it while the game's intended shaders, constants, targets, and material resources are
   bound.
6. **Suppress** the original geometry for the same logical replacement.

Recover and skinning work should normally execute once per animation sample, while the draw should
execute in every compatible pass in which the original host draws. Shadows, depth, and outlines are
often separate draws rather than noise to suppress.

That optimization has an important scope: “once” means once per live model instance and pose, not
necessarily once per Present call. See the multiple-instance section below.

### 3.1 Resource lifetime and temporal coherence

Runtime correctness depends on both resource semantics and sample time.

| Operation | What it preserves | Main failure mode |
| --- | --- | --- |
| Reference/alias | The resource object; later reads see its then-current contents | The engine may reuse, rename, window, or update it at another time |
| Copy/snapshot | The bytes visible at the copy event | A consumer can run before this frame's producer and receive the previous snapshot |
| Persistent output | The last value written by the mod | Another instance, scene, or stale source can reuse it |

**Principle:** Every value combined in one recovery and conversion must describe a coherent animation
sample. “Captured at its own draw” is not sufficient when the consuming chain runs at another draw.

**Measure:** Determine when each posed buffer is written, whether it is stable across the frame's
passes, whether several renderers alias the same allocation, and whether constant-buffer subranges
are used.

**Measure:** Record draw order in every important scene. A source that draws before the anchor in one
scene may draw after it in another.

Common ways to establish coherence are:

- Defer consumption until every required source has produced a sample for the current instance.
- Recover and store source rows at the source draw, then consume only after a current-sample barrier.
- Derive object-space conversion from same-sample recovered geometry.
- Restrict the pool to sources whose resource update and draw schedule make coherence provable.

A copied per-draw constant is safe only when its sample is proven coherent with the posed positions
being recovered. A referenced posed buffer is current only when the engine's update behavior has been
measured to make it so.

### 3.2 State hygiene

Save and restore by reference every binding the replacement changes. Clearing a slot to null is not a
restore; later game draws may rely on the previous binding.

Treat render targets, depth state, viewports, UAVs, vertex and index buffers, shader resources,
samplers, and modified constants according to the interposer's actual save/restore semantics.

## 4. Problem classes

### 4.1 The replacement uses bones its target does not carry

*Typical symptom: a replacement follows its host near the attachment point, but distant regions
freeze, collapse, or ride the wrong body segment because the host never supplied those bones.*

A single mesh constrains only the bones represented by its weighted vertices. A coat, long hair, or a
body-spanning donor may therefore need several source meshes.

Build a pool and a union palette:

1. Identify every bone used materially by the replacement.
2. Find admissible source parts that actually pose those bones.
3. Recover each source into a shared union layout.
4. Assign one writer for every union bone.
5. Give each source a scatter map; rows it does not own use a sentinel and are not written.

**Principle:** One row needs one authoritative writer within a pipeline. Two approximate recoveries
must not race to overwrite it.

A pool candidate is not admissible merely because it renders or lists the bone. It must satisfy the
relevant conditions:

- Its skin is readable and its posing model is compatible.
- The bone has nonzero weighted support and passes the conditioning gate, or an explicit approximation
  has been accepted.
- The source is present whenever the replacement needs it, or a defined absence fallback exists.
- Its required detail tier draws in the same context.
- Its sample can be made temporally coherent with the consumer.
- Its bind and draw spaces can be converted safely.
- Its bone identity belongs to the same rig rather than merely sharing a name or hash.

Bone coverage is not guaranteed by the union of whatever happens to render. A bone can drive a child
transform or attachment without weighting an admissible source mesh. A materially used bone with
neither a sound recovery nor an explicitly accepted approximation must refuse the build.

### 4.2 Choosing the owner of an overlapping bone

*Typical symptom: deformation is correct while one source is visible, then snaps, freezes, or takes a
rigid fallback when that source disappears, even though another pooled part also carries the bone.*

Summed vertex weight is a useful support heuristic, but it is not the primary reliability rule.

A robust selection is:

1. Discard sources that cannot meet the presence, scheduling, bone-identity, and space-conversion
   requirements.
2. If the pipeline anchor recovers the row soundly, give it ownership; its draw is the event that
   requires the replacement.
3. Otherwise choose the sound admissible source with the strongest geometric support.
4. If none is sound, use only an explicitly accepted approximation; otherwise refuse the dependency.

**Policy:** The production implementation gives the anchor every bone its operator
recovers soundly. For a bone the anchor cannot constrain, the summed-weight owner remains; if
that owner's operator is weak, its configured rigid tie may stand in for recovery.

Ownership is local to one pipeline. Two independently hosted replacements do not need the same owner
for a shared bone. They need their final posed geometry to agree in world space.

### 4.3 Pooled parts use different draw spaces

*Typical symptom: the replacement develops a slow lean or positional drift relative to the body, or
a split boundary opens farther as the character moves, rather than exploding immediately.*

A recovered palette maps bind space into the source renderer's posed object space. Two renderers on
one character need not use the same object-to-world transform.

For a row recovered from owner part P and consumed at anchor A:

    row_anchor = row_part · K
    K = W_part · inverse(W_anchor)

where W is object-to-world in row-vector convention.

**Principle:** The source palette and K must describe the same animation sample. A correct formula
with a previous-frame W is still wrong.

#### Conversion from draw constants

Per-draw object transforms can provide K when:

- The correct logical constant range can be addressed.
- Both transforms are snapshots of the intended source and anchor.
- Their sample times are coherent with the recovered posed buffers.
- The matrices are invertible and use the established convention.

Constant copies are not made coherent merely by being copied at their respective draws. If the
anchor consumes before the source draws this frame, the source copy is stale.

#### Conversion from a geometry witness

When constants are unreadable, incorrectly windowed, or not provably coherent, use a shared witness
bone that both parts recover:

    K = inverse(M_witness_part) · M_witness_anchor

The witness bone must:

- Be the same stable bone identity on both parts.
- Be expressed in reconciled bind spaces.
- Be sound in every operator and tier that uses it.
- Be recovered from the same animation sample on both sides.
- Remain safely invertible over the supported animations.

One valid witness matrix determines K. During porting, compare K from several candidate witness bones
over captured poses; disagreement reveals a bad identity, bind-space conversion, operator, or sample.

**Policy:** When no sound witness exists, the production implementation falls back to constant
conversion and names that dependency in the build log as riding draw order. A port that cannot
prove or diagnose the fallback should refuse the cross-space dependency instead.

### 4.4 Parts were authored in different bind spaces

*Typical symptom: one pooled part is rotated by a quarter-turn, lies face-down, or deforms around a
consistent wrong axis while the other parts animate normally.*

Pooling keeps one bind statement per bone, so all source geometry and bind poses must be expressed in
one reference space, normally the anchor's.

In general, a consistent invertible basis change can be applied to geometry and bind poses together.
The exact formulas depend on the engine's matrix convention. The key invariant is that the
bone-space quantity consumed by skinning remains unchanged.

**Principle:** A valid basis change is uniform across corroborating shared bones. A transform inferred
from one coincidentally matching bone is not evidence.

**Policy:** The production implementation first composes measured scene-rest transforms for the part
and reference. That route needs no shared bones, but the resulting relation must still snap to the
supported pure axis-aligned signed-permutation rotation. When either measured rest is unavailable,
the fallback fits the relation over shared bind poses and requires at least three corroborating
bones. Translation, arbitrary rotation, scale, shear, nonuniform deltas, and weakly corroborated
fallback matches are refused. That narrow gate reflects measured asset conventions, not a universal
mathematical restriction.

Stable bone identifiers can collide across unrelated rigs. Validate identity using bind agreement,
rig structure, and multiple shared bones rather than trusting a hash alone.

### 4.5 Some bones are numerically unrecoverable

*Typical symptom: a small or decimated region flickers, stretches violently, or sends a limb away
from the character only in particular poses, while most of the mesh remains correct.*

For an unconstrained affine palette row, the local weighted homogeneous positions must span rank four.
Four vertices are a minimum in the simple case, but vertex count alone is not sufficient: coplanar
positions, trace weights, and proportional co-weighting can leave a bone unidentifiable.

Gate each bone at build time using:

- A dense recovery evaluated after conversion to the precision that ships.
- Synthetic palettes that exercise rotation-like and translation terms.
- A left-inverse defect for any reduced operator.
- Explicit NaN, infinity, singular-value, and support checks.

A weak bone may ride a sound nearby or co-weighted bone through a rigid tie. That is an approximation,
not recovered animation.

**Policy:** Ties are acceptable only when the replacement places negligible weight on the weak bone
or the visual rigid ride is explicitly accepted. Material replacement weight on an unsupported bone
should refuse the build.

### 4.6 Dense operators are too large

*Build-time signature: operator buffers dominate the mod size and multiply with every pooled part and
detail tier, even though most bones depend strongly on only a small subset of vertices.*

A dense pseudoinverse stores four rows per bone across every source vertex. Large meshes and several
detail tiers can make that expensive.

A reduced operator can select a per-bone subset:

- Strongly weighted vertices distributed through bind space.
- Discriminator vertices that constrain co-bones without carrying the target bone.
- A widening schedule for bones that fail the local gate.
- Dense-width fallback for an individual bone when reduction does not hold.

Gate the reduced rows on their left-inverse defect, not only on one synthetic pose. A truncated solve
can reproduce one palette while failing another.

**Measure:** Report reduction ratios with the corpus, vertex counts, bone counts, thresholds, and
buffer accounting that produced them. A number such as “35× smaller” is an observation, not a
portable expectation.

### 4.7 Each part has several detail tiers

*Typical symptom: the replacement pops back to vanilla, loses a region, or changes deformation
exactly when distance, a preview, or another rendering context selects a different tier.*

Every independently rendered tier is a different source mesh with its own:

- Match signature.
- Vertex order and recovery operator.
- Weighted bone support.
- Conditioning result.
- Bind-space evidence.
- Draw schedule and possible constant-buffer layout.

Cover every renderable tier required by the replacement. A tier can pose a bone absent from every
pooled top-detail mesh; it then needs an eligible carrier that both poses the bone and has a
corresponding tier in that context.

Do not assume a top-detail row can stand during a lower-tier draw. Reuse is safe only when its sample,
placement, and space conversion remain valid. Otherwise recover or tie the row in the tier's own
chain.

Detail tiers often make geometry witnesses more important because their constant bindings and draw
order can differ from the top-detail renderer.

### 4.8 A recovery source can be absent

*Typical symptom: the replacement works in one outfit or scene but freezes, collapses, or inherits an
old pose when an accessory, wardrobe option, or context-specific source stops drawing.*

A palette segment is only as fresh as the last admissible sample written by its source.

**Presence rule:** A part may feed another part's replacement only when it is present whenever that
replacement needs the row, or when absence has an explicit safe fallback.

Classify presence along the game's real axes: scene, wardrobe option, combat state, cinematic state,
visibility animation, and any other mechanism observed to subtract draws. Unclassifiable parts may
still host their own replacement, but should not be trusted as another part's source.

Possible absence behavior includes:

- Refuse the dependency.
- Prefer a sound anchor recovery.
- Tie the absent source's donor-used rows to sound anchor-owned ancestors.
- Seed rows to identity only when bind placement is an intentional fallback.

Presence latches are themselves sampled state. A previous-frame latch can be useful, but its delay and
transition behavior must be tested.

### 4.9 Off-camera and shadow behavior

*Typical symptom: animation remains correct while the source is visible, then becomes stale when the
camera turns away, or changes behavior when shadow distance and camera framing change.*

Some games continue to submit an off-camera renderer to a shadow pass; others cull it, cache the
shadow, restrict it by cascade, or never submit it.

**Measure:** Trace real draw submission. A serialized “casts shadows” flag alone does not prove that a
source refreshes off camera.

**Policy:** In the production corpus, a measured shadow-casting Off disqualifies a
part from feeding another part or serving as a presence witness. Its own replacement, hide, and
retexture remain valid because those operations occur only at its own draw.

### 4.10 Scene logic hides parts independently of their names

*Typical symptom: a replacement fails only in a dorm, lobby, interaction, cinematic, or animation
clip even though its mesh name and selected outfit are unchanged.*

Do not infer presence only from mesh names or wardrobe slots. Search the game's authored data and
runtime traces for systems that can subtract a draw: node visibility lists, scene overrides,
animation events, preview modes, and option-specific companions.

Demote only a part that measured data actually names or a runtime trace proves conditional. Failure
to read an optional visibility source should not silently classify every part as absent.

### 4.11 Different meshes share a match key

*Typical symptom: enabling one replacement also suppresses or redraws another outfit, variant, or
semantic part whose runtime section happens to match the same hashed content.*

Content-derived hashes identify content, not necessarily one semantic mesh or one live instance.
Games reuse buffers and index data.

Use a discriminator ladder:

1. Another bound buffer whose content differs.
2. A uniquely tagged texture or material resource.
3. A sibling draw that unambiguously establishes a variant or context.
4. Another stable runtime signal.
5. Refusal when the draws remain indistinguishable.

Build sections around the complete signature actually needed to prevent cross-fire. Assume from the
start that one hash can identify several semantic draws.

### 4.12 Meshes store different influence widths

*Typical symptom or build signature: a rigid prop is mistaken for an especially strong deformer, a
narrow skin is rejected as incomplete, or widening/reduction silently changes its deformation.*

One-, two-, three-, and four-influence skins can all be valid if the stored stream is complete.
Verify the format semantics and weight sums before widening. Padding zero-weight slots into a
canonical layout is lossless.

One-influence meshes often accumulate high summed support because every vertex rides one bone at
weight 1. That does not make them bad sources, but it makes weight-only ownership misleading.
Evaluate their conditioning, presence, tier schedule, and sample freshness.

**Policy:** The production corpus conservatively keeps one-influence parts out of
other parts' pools. A genuine multi-influence split pools normally.

### 4.13 Posing models and layouts the implementation refuses

*Build-time signature: the source replay has structural residual, the skin stream cannot be decoded
losslessly, or the part carries morph, cloth, physics, or procedural deformation outside the
implementation's supported model.*

Keep the routing predicate centralized so the UI, build, and runtime emission cannot disagree.

- **Blend-shape carriers:** A static C does not model changing morph positions or post-skin morph
  deltas. Supporting them requires the morph coefficients, deltas, ordering, and a verified combined
  model. This implementation refuses replacement while allowing hide and retexture.
- **Runtime-physics rigs:** Physics-driven bone transforms can still be recoverable LBS rows.
  Refusing them is a quality policy when the replacement was not designed and tested against the
  original simulation. It is not a mathematical limitation of palette recovery.
- **Unreadable skin layouts:** Packed, shared, or unusual formats are acceptable only when they can
  be decoded losslessly and aligned to vertices. Refuse rather than guess at a stride or semantic.
- **True statics:** A skinless mesh has no palette to recover and needs none. Route it through a direct
  geometry replacement using the draw's existing object transform.
- **Other vertex deformation:** Cloth simulation, procedural vertex motion, and post-skin offsets need
  their own captured state or model. Do not silently absorb them into bone error.

### 4.14 Edits must survive a DCC round trip

*Typical symptom or build signature: bones return reordered or renamed, vertices lose all supported
weight, or the build reports influences painted to bones the target rig cannot resolve.*

Joint-array position is not a stable identity. Preserve a stable game bone identifier as metadata or
in a controlled joint-name token, then resolve the returned skin by that identifier.

If the game uses hashes, validate collisions rather than assuming the hash is globally unique.
Preserve enough rig and bind context to diagnose an ambiguous identifier.

Define unresolved influence behavior explicitly:

- If a vertex retains some supported weighted influences, drop unsupported ones, renormalize the
  survivors, and warn.
- If every weighted influence is unsupported, do not ship an all-zero skin. Refuse, or apply a clearly
  documented fallback such as nearby original weights.
- Report the number of affected vertices, missing bones, and materially dropped weight.
- Reducing a four-wide authored skin to a narrower target should keep the strongest influences,
  renormalize, and warn whenever nonzero weight was discarded.

Tolerant behavior is a policy, not an excuse to hide deformation. Warnings must be visible at build
time and specific enough for the author to repaint the mesh.

### 4.15 One continuous donor surface is split across host pipelines

*Typical symptom: a thin shading line remains at rest, while a positional gap opens and grows under
motion even though the duplicated boundary positions and weights appear identical in the DCC tool.*

This is the seam-critical case: one authored surface is divided because the game renders its regions
as separate parts, anchors, or replacement pipelines.

Before separation, record explicit boundary-pair identity. Coincident positions found later by a
tolerance are not enough when several duplicated vertices share one location.

For every paired boundary vertex, verify:

- Bind positions agree in one declared space.
- Bone identities and effective normalized weights agree; local joint indices may differ.
- Both pipelines recover or approximate every materially weighted bone.
- Bind-space conversions are compatible.
- Final posed positions agree in world space across a representative motion set.
- The two chains consume temporally coherent samples.

Identical local positions and weights do not guarantee closure. If the pipelines use different
anchors, their final equality is:

    posed_left · W_left = posed_right · W_right

A stale or inconsistent conversion opens the surface even when every authored boundary value is
bit-identical.

Do not require the same global bone owner across independent pipelines merely to close the seam.
Different sound recoveries are valid when their converted world result agrees. Global re-ownership
can weaken presence fallbacks without improving the boundary.

Treat positional and shading continuity separately:

- Preserve or jointly construct normals across the unsplit surface before duplication.
- Preserve tangent direction and handedness across the boundary.
- Preserve any outline, shell, or custom vertex direction field the game consumes.
- Independent normal recalculation after separation normally creates a shading seam even when the
  positions remain closed.

Acceptance should measure paired world-space separation over several high-motion poses and report at
least median, p95, and maximum error. Inspect normal and tangent angles independently.

### 4.16 Several live instances share the same resources

*Typical symptom: the second copy of a character adopts the first copy's pose, or whichever instance
draws first controls the replacement output for every later instance in the frame.*

Content hashes and global mod variables do not identify a character instance. If two actors with the
same source meshes draw in one frame, a global “done this frame” flag and one persistent output buffer
can make the second actor reuse the first actor's pose.

**Measure:** Test duplicate actors, mirrors, previews, photo mode, co-op, enemies using player assets,
and any scene that renders the model twice.

Safe designs need one of:

- A reliable instance discriminator and per-instance state.
- Recompute at every logical anchor draw while distinguishing repeated passes of one instance.
- An engine-provided instance index that can address separate output regions.
- A documented refusal or limitation to one live instance.

“Once per frame” is sound only after the instance scope has been proven.

### 4.17 The host renderer controls submission and bounds

*Typical symptom: added geometry disappears near the edge of the camera, fails to appear in a
reflection, or stops casting a shadow while the original host's smaller bounds are culled.*

An interposer override normally runs only after the engine submits the original host. The host's
visibility tests, local bounds, occlusion state, LOD choice, and pass participation therefore gate the
replacement.

Replacement geometry extending beyond the host's bounds can disappear at camera edges or fail to cast
expected shadows. A replacement cannot appear in a reflection, motion-vector pass, or auxiliary
camera that never submits its host.

**Measure:** Test the replacement's full extent against camera frusta, occlusion, shadow cameras,
reflection cameras, and detail transitions. If bounds cannot be expanded in engine data, choose a host
whose submission conservatively covers the replacement or document the limitation.

### 4.18 Positions are not the whole shading contract

*Typical symptom: the silhouette and animation are correct, but a dark seam, broken normal map,
incorrect outline, or temporal smear remains.*

Recovering positions proves nothing by itself about normals, tangents, outline fields, motion vectors,
or material-specific vertex data.

For rigid or uniform-scale bone transforms, applying the palette's linear part with homogeneous w=0
and normalizing is often sufficient for normals and tangents. With nonuniform scale or shear, correct
normal handling may require inverse-transpose logic or exact reproduction of the game's deformation
path.

Validate:

- Normal and tangent skinning against captured originals.
- Tangent handedness and normal-map orientation.
- Outline or shell direction channels.
- Material subdivision and per-submesh textures.
- Depth, shadow, outline, reflection, and motion-vector passes.
- Previous-pose data where temporal effects require it.

Reusing the game's bound shader provides its lighting environment; it does not automatically satisfy
the shader's complete input and temporal contract.

## 5. Hard limits and scoped limitations

- **Recovered animation is capped by observed animation sources.** The recovered palette supplies no
  motion for a bone the game never poses. Extra bones require another authored, procedural, or
  captured animation source outside this technique.
- **A replacement depends on host submission.** It cannot appear in a context where no usable host
  draw occurs.
- **A source updates only when the measured resource or draw event updates it.** Persistence is not
  freshness.
- **Temporal coherence cannot be repaired by correct matrix algebra.** If two inputs describe
  different samples, their combination is wrong.
- **Operator quality is bounded by source information and shipped precision.** Quantization and rank
  loss cannot be wished away by a higher-precision offline prototype.
- **A continuous split needs world-space agreement.** Matching authoring data alone does not establish
  that agreement.
- **One global output is not automatically multi-instance safe.**
- **Unsupported deformation models must be incorporated explicitly or refused.**

## 6. Acceptance and observability

Use thresholds relative to mesh scale and the visual requirement; there is no universal epsilon.
Retain enough artifacts to reproduce every gate.

### 6.1 Asset and stream validation

- Decode positions, skin, bind poses, and indices independently of the runtime path.
- Prove vertex-order alignment.
- Verify weight sums and stored influence semantics.
- Detect duplicate or ambiguous bone identities.
- Confirm every donor-used bone is covered.

### 6.2 Recovery validation

- Capture several distinct poses.
- Report source forward-replay residuals.
- Report per-bone synthetic recovery error at shipped precision.
- Report rank or left-inverse defect for reduced operators.
- Name every weak, tied, dense-width, or uncovered bone.

### 6.3 Scheduling validation

- Trace source and anchor draw order in every important scene.
- Record which frame or animation sample each captured resource represents.
- Exercise a case where a source draws after the anchor.
- Treat a constant fallback as unsafe until this trace proves otherwise.

### 6.4 Space-conversion validation

- Independently derive the matrix convention.
- Compare constant-derived and geometry-derived K where both are available and coherent.
- Compare several witness bones over several poses.
- Reject singular witnesses and unexplained disagreement.

### 6.5 Output validation

- Forward-skin the original source mesh through the exact emitted operator and shader arithmetic.
- Compare replacement positions in world space, not only in each local anchor space.
- Test every render pass the host participates in.
- Test camera edges, shadows, reflections, and detail transitions.
- Test more than one live instance.

### 6.6 Split-surface validation

- Keep an explicit boundary-pair ledger.
- Compare effective weights by bone identity.
- Measure world-space separation over high-motion poses.
- Report median, p95, and maximum.
- Compare normal and tangent angles separately.
- Fix isolated authoring defects in the authored mesh, not in generated buffers.

Generated diagnostics should state when a build:

- Falls back to draw-order-dependent constants.
- Reuses a lower- or higher-tier row.
- Ties or identity-seeds a bone.
- Depends on a presence latch.
- Cannot establish a same-frame witness.
- Is limited to one live instance.

A silent fallback is part of the deformation whether or not the user knows it happened.

## 7. Order of attack

Prove the mechanism one rung at a time in the actual game:

1. **Hide:** match and suppress the intended draw.
2. **Same-count substitution:** prove stream layout and draw routing.
3. **Static replacement draw:** prove custom buffers, indices, materials, and state restoration.
4. **Fixed-palette skinning:** prove replacement skinning and normal/tangent handling.
5. **Live single-part recovery:** prove LBS compatibility and operator identifiability.
6. **New topology:** prove bounds, passes, and material subdivision.
7. **Pooled recovery:** add ownership, presence, and space conversion.
8. **Detail tiers:** prove every tier's support and scheduling.
9. **Split surfaces:** prove world-space boundary closure and shading continuity.
10. **Multiple instances and scenes:** prove the lifetime and state model beyond the first success case.

At every rung, test more than one pose and preserve the smallest capture that disproves an assumption.
Prefer a refused build with a specific reason over a character that deforms subtly and only in one
scene.
