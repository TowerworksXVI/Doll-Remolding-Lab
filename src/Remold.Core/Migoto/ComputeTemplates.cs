using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Remold.Core.Migoto;

/// <summary>
/// The stamped HLSL compute shaders of the pooled swap — recover, convert, skin. Emitted verbatim with
/// counts/offsets/cbuffer sizes stamped in; the emitted bytes are the swap's emission contract and must
/// not be altered.
/// </summary>
public static class ComputeTemplates
{
    // %(...)d / %(...)s tokens are stamped by the Emit* methods; line endings are normalised to LF on
    // emit so output is byte-stable regardless of this file's on-disk endings

    // A convert dispatch gives each part it converts a constant buffer of its own, from this register up;
    // b13 is the anchor's and the shader's other bindings sit below b5.
    const int FirstPartRegister = 5, LastPartRegister = 12;

    /// <summary>Parts one convert dispatch binds constants for: the width of a convert CHUNK. A
    /// REGISTER-RANGE fact, not a pool limit: <see cref="EmitConvert"/> lays each part of its chunk out at a
    /// register of its own, the shader has exactly this many free before b13, the anchor's, and a larger
    /// pool converts in <see cref="ConvertChunks"/> dispatches.</summary>
    public const int PartsPerConvert = LastPartRegister - FirstPartRegister + 1;

    /// <summary>The convert dispatches a pool of <paramref name="partCount"/> parts runs: one per
    /// <see cref="PartsPerConvert"/> parts in pool order, and always at least one.</summary>
    public static int ConvertChunks(int partCount) =>
        Math.Max(1, (partCount + PartsPerConvert - 1) / PartsPerConvert);

    /// <summary>The pool parts convert chunk <paramref name="chunk"/> binds and converts: part indices
    /// [<c>First</c>, <c>End</c>) in pool order.</summary>
    public static (int First, int End) ConvertChunkParts(int partCount, int chunk)
    {
        int chunks = ConvertChunks(partCount);
        if (chunk < 0 || chunk >= chunks)
            throw new ArgumentOutOfRangeException(nameof(chunk), chunk,
                $"a {partCount}-part pool converts in {chunks} chunk(s)");
        int first = chunk * PartsPerConvert;
        return (first, Math.Min(partCount, first + PartsPerConvert));
    }

    /// <summary>The constant-buffer register pool part <paramref name="partIdx"/> is bound at in its
    /// convert chunk's dispatch: b5 for each chunk's first part, upward from there.</summary>
    public static int PartRegister(int partIdx) => FirstPartRegister + partIdx % PartsPerConvert;

    public const string RecoverTemplate =
@"// Pooled recover for one part: a = Cpinv * posed(anchor verts), SCATTERED into the shared union
// palette via Map (part-local bone -> union bone). The operator is SLIM: each bone reads only its
// own anchor vertices (Sel), not the whole mesh. Widths are RAGGED — Off carries (base,width) per
// bone, so a bone that needs every vertex costs its neighbours nothing. Rows land in THIS PART'S
// draw space; the convert pass rebases them into the anchor's space before skinning.
// ROWS = 4*partBones.
struct Vtx { float3 position; float3 normal; float4 tangent; };
StructuredBuffer<Vtx> q      : register(t0);
Buffer<float>         Cpinv  : register(t1);   // per bone: 4 rows of `width` coefficients, from 4*base
Buffer<uint>          Map    : register(t2);   // partBones entries: local bone -> union bone, or 0xFFFFFFFF
Buffer<uint>          Sel    : register(t3);   // anchor vertex indices, bone b at [base, base+width)
Buffer<uint>          Off    : register(t4);   // 2 per bone: base, width
RWStructuredBuffer<float4> palOut : register(u1); // 4*unionBones rows (shared across all pool parts)
static const uint ROWS=%(ROWS)d;
[numthreads(64,1,1)]
void main(uint3 tid : SV_DispatchThreadID){
    uint i=tid.x; if(i>=ROWS) return;
    uint localBone=i>>2, comp=i&3;
    uint u=Map[localBone];
    if(u==0xFFFFFFFF) return;   // this part does NOT own the bone (another part supports it better) -> don't clobber
    uint sbase=Off[localBone<<1], width=Off[(localBone<<1)|1];
    // These pseudo-inverse rows can be ill-conditioned. Keep the reduction ordered and compensate
    // its rounding error so driver-specific contraction/reassociation cannot amplify it into the pose.
    precise float3 a=float3(0,0,0), correction=float3(0,0,0);
    uint cbase=(sbase<<2)+comp*width;
    for(uint t=0;t<width;t++){
        precise float3 term=Cpinv[cbase+t]*q[Sel[sbase+t]].position;
        precise float3 y=term-correction;
        precise float3 next=a+y;
        correction=(next-a)-y;
        a=next;
    }
    palOut[(u<<2)|comp]=float4(a,(comp==3)?1.0:0.0);   // scatter into this bone's union slot
}
";

    public const string RecoverDenseTemplate =
@"// Pooled recover for one part: a = Cpinv * posed, SCATTERED into the shared union palette
// via Map (part-local bone -> union bone). DENSE operator: the slim layout would not have been
// smaller for this part, so each row spans every vertex. Rows land in THIS
// PART'S draw space; the convert pass rebases them into the anchor's space before skinning.
// ROWS = 4*partBones; N = this part's verts.
struct Vtx { float3 position; float3 normal; float4 tangent; };
StructuredBuffer<Vtx> q      : register(t0);
Buffer<float>         Cpinv  : register(t1);
Buffer<uint>          Map    : register(t2);   // partBones entries: local bone -> union bone, or 0xFFFFFFFF
RWStructuredBuffer<float4> palOut : register(u1); // 4*unionBones rows (shared across all pool parts)
static const uint N=%(N)d, ROWS=%(ROWS)d;
[numthreads(64,1,1)]
void main(uint3 tid : SV_DispatchThreadID){
    uint i=tid.x; if(i>=ROWS) return;
    uint localBone=i>>2, comp=i&3;
    uint u=Map[localBone];
    if(u==0xFFFFFFFF) return;   // this part does NOT own the bone (another part supports it better) -> don't clobber
    // These pseudo-inverse rows can be ill-conditioned. Keep the reduction ordered and compensate
    // its rounding error so driver-specific contraction/reassociation cannot amplify it into the pose.
    precise float3 a=float3(0,0,0), correction=float3(0,0,0);
    uint base=i*N;
    for(uint v=0;v<N;v++){
        precise float3 term=Cpinv[base+v]*q[v].position;
        precise float3 y=term-correction;
        precise float3 next=a+y;
        correction=(next-a)-y;
        a=next;
    }
    palOut[(u<<2)|comp]=float4(a,(comp==3)?1.0:0.0);   // scatter into this bone's union slot
}
";

    /// <summary>The affine inverse every rebase shader states its K with, row-vector convention: the
    /// rows of the inverted 3x3 and the translation carried back through them. One body for every shader
    /// that rebases rows between two draws' spaces.</summary>
    const string AffineInverseFn =
@"float4x4 AffineInverse(float4 r0, float4 r1, float4 r2, float4 r3){
    float3 a=r0.xyz, b=r1.xyz, c=r2.xyz, t=r3.xyz;
    float3 bc=cross(b,c), ca=cross(c,a), ab=cross(a,b);
    float det=dot(a,bc);
    float3 i0=float3(bc.x,ca.x,ab.x)/det;    // rows of R^-1 (row-vector convention)
    float3 i1=float3(bc.y,ca.y,ab.y)/det;
    float3 i2=float3(bc.z,ca.z,ab.z)/det;
    float3 ti=-(t.x*i0+t.y*i1+t.z*i2);
    return float4x4(float4(i0,0),float4(i1,0),float4(i2,0),float4(ti,1));
}
";

    public const string ConvertTemplate =
@"// Rebase every union bone's palette rows from its OWNER part's draw space into the
// anchor's:  row' = row . K,  K = W_owner . inverse(W_anchor).  W = objectToWorld = vs-cb1 c0..c3,
// row-vector convention (c4..c7 is the root inverse, NOT necessarily this cb's own inverse, so
// the inverse is computed here instead of read).
// Runs at the anchor draw, where every part's cb was captured at the same draw as its vb0 — a
// segment and its K always come from the same frame, so conversion never mixes frames.
%(PART_CBUFFERS)s
cbuffer AnchorCB : register(b13) { float4 WA[4]; }   // parts occupy b5..b12 (max 8), anchor b13
StructuredBuffer<float4> palRaw    : register(t0);
Buffer<uint>             ownerPart : register(t1);   // per union bone: owning part index
RWStructuredBuffer<float4> palOut  : register(u1);
static const uint ROWS=%(ROWS)d;

" + AffineInverseFn + @"
[numthreads(64,1,1)]
void main(uint3 tid : SV_DispatchThreadID){
    uint i=tid.x; if(i>=ROWS) return;
    uint bone=i>>2;
    uint pi=ownerPart[bone];
    float4x4 WP;
%(PART_SELECT)s
    float4x4 K=mul(WP, AffineInverse(WA[0],WA[1],WA[2],WA[3]));
    palOut[i]=mul(palRaw[i], K);
}
";

    public const string ConvertWitnessTemplate =
@"// Rebase every union bone's palette rows from its OWNER part's draw space into the anchor's,
// with K solved from GEOMETRY instead of draw constants: each non-anchor part designates a
// WITNESS bone it shares with the anchor. Both parts recover that bone (each in its own space);
// the recoveries land in reserved palette slots via the scatter maps, and
// K = inverse(M_witness_part) . M_witness_anchor. Constants stay untouched — some renderers
// (the battle movement-preview hologram) bind vs-cb1 as a WINDOW into one shared buffer
// (VSSetConstantBuffers1 FirstConstant), which a whole-resource copy cannot see through.
StructuredBuffer<float4> palRaw    : register(t0);   // 4*(unionBones + witness slots) rows
Buffer<uint>             ownerPart : register(t1);   // per union bone: owning part index
RWStructuredBuffer<float4> palOut  : register(u1);
static const uint ROWS=%(ROWS)d;      // 4*unionBones — witness slots beyond are inputs only
static const uint ANCHOR=%(ANCHOR)d;
static const uint2 WIT[%(P)d] = { %(WIT)s };   // per part: base row of (partSide, anchorSide) witness; x=0xFFFFFFFF = no witness (keep the row as-is)

" + AffineInverseFn + @"
[numthreads(64,1,1)]
void main(uint3 tid : SV_DispatchThreadID){
    uint i=tid.x; if(i>=ROWS) return;
    uint bone=i>>2;
    uint pi=ownerPart[bone];
    if(pi==ANCHOR || pi>=%(P)d || WIT[pi].x==0xFFFFFFFF){ palOut[i]=palRaw[i]; return; }
    uint2 w=WIT[pi];
    float4x4 MP=float4x4(palRaw[w.x],palRaw[w.x+1],palRaw[w.x+2],palRaw[w.x+3]);
    float4x4 MA=float4x4(palRaw[w.y],palRaw[w.y+1],palRaw[w.y+2],palRaw[w.y+3]);
    float4x4 K=mul(AffineInverse(MP[0],MP[1],MP[2],MP[3]), MA);
    palOut[i]=mul(palRaw[i], K);
}
";

    // The two row-solve bodies a group member's fused shader and a copy-cache kernel are stamped with — the
    // same operator layouts the recover shaders read, wrapped as a function so the witness variant can call
    // it for the witness bone as well as for the group bone the thread owns. Each reads vertex i's position
    // as POS(i), which the shader stamping the body defines for how it holds the vertices. A row is a long
    // chain of dependent reads in one thread, so each body fetches eight terms' inputs before it adds any of
    // them: the reads of a batch are in flight together, and the additions run one at a time in the same
    // order as an unbatched sum, so the row is the same to the bit.
    const string GroupRowSlim =
@"Buffer<uint>          Sel    : register(t3);   // the vertices each bone reads, bone b at [base, base+width)
Buffer<uint>          Off    : register(t4);   // 2 per bone: base, width
float4 Row(uint b, uint comp){
    uint sbase=Off[b<<1], width=Off[(b<<1)|1];
    precise float3 a=float3(0,0,0), correction=float3(0,0,0);
    uint cbase=(sbase<<2)+comp*width;
    uint t=0;
    for(;t+8<=width;t+=8){
        uint s[8]; float c[8]; float3 p[8];
        [unroll] for(uint k0=0;k0<8;k0++){ s[k0]=Sel[sbase+t+k0]; c[k0]=Cpinv[cbase+t+k0]; }
        [unroll] for(uint k1=0;k1<8;k1++) p[k1]=POS(s[k1]);
        [unroll] for(uint k=0;k<8;k++){
            precise float3 term=c[k]*p[k];
            precise float3 y=term-correction;
            precise float3 next=a+y;
            correction=(next-a)-y;
            a=next;
        }
    }
    for(;t<width;t++){
        precise float3 term=Cpinv[cbase+t]*POS(Sel[sbase+t]);
        precise float3 y=term-correction;
        precise float3 next=a+y;
        correction=(next-a)-y;
        a=next;
    }
    return float4(a,(comp==3)?1.0:0.0);
}";

    const string RowAdd =
@"            precise float3 y=term-correction;
            precise float3 next=a+y;
            correction=(next-a)-y;
            a=next;";

    public const string GroupFuseTemplate =
@"// One wardrobe-group member's FALLBACK dispatch, fused: recover this group's bones from the MEMBER's
// posed vertices and rebase the rows into the anchor's draw space in the same dispatch. This variant
// runs at the member's OWN draw — the one placement where its constants copy and its geometry are
// same-frame by construction — and only for a lod0 sharing no sound bone with the anchor, where no
// geometric K exists; its write order against the anchor's chain follows the frame's draw order.
// Rows land in the group's APPENDED slot region of the CONVERTED palette, past the union and the
// witness slots; the convert passes write only union rows, so the copy round-trip carries these
// through untouched.
// K comes from CONSTANTS: W_member is this draw's own vs-cb1, W_anchor the anchor's captured one.
// ROWS = 4*groupBones; BASE = the group's first appended slot.
struct Vtx { float3 position; float3 normal; float4 tangent; };
StructuredBuffer<Vtx> q      : register(t0);   // the member's posed vb0, captured at this draw
Buffer<float>         Cpinv  : register(t1);
Buffer<uint>          Map    : register(t2);   // per GROUP bone: this member's local bone, or 0xFFFFFFFF
RWStructuredBuffer<float4> palOut : register(u1);
cbuffer MemberCB : register(b5)  { float4 WM[4]; }
cbuffer AnchorCB : register(b13) { float4 WA[4]; }
static const uint ROWS=%(ROWS)d, BASE=%(BASE)d;
#define POS(i) q[i].position
%(ROWFN)s

" + AffineInverseFn + @"
[numthreads(64,1,1)]
void main(uint3 tid : SV_DispatchThreadID){
    uint i=tid.x; if(i>=ROWS) return;
    uint g=i>>2, comp=i&3;
    uint b=Map[g];
    if(b==0xFFFFFFFF) return;   // this member cannot condition the bone -> leave the row alone
    float4x4 K=mul(float4x4(WM[0],WM[1],WM[2],WM[3]), AffineInverse(WA[0],WA[1],WA[2],WA[3]));
    palOut[((BASE+g)<<2)|comp]=mul(Row(b,comp), K);
}
";

    public const string GroupFuseWitnessTemplate =
@"// One wardrobe-group member mesh's fused recover+rebase, run from the ANCHOR's chain gated on the
// mesh's presence latch (last frame's draw stream): recover this group's bones from the MEMBER's
// posed vertices — a by-ref capture, current-frame at the chain — and rebase the rows into the
// anchor's draw space in the same dispatch.
// K comes from GEOMETRY, not constants: in the chain the member's constants copy is from its own
// last draw (frame-mixing), and tier renderers can bind vs-cb1 as a window into a shared buffer a
// whole-resource copy reads wrongly. Both sides recover one WITNESS bone the member and the anchor
// pose soundly — the member's side inline here (a UAV write another thread makes is not readable in
// the same dispatch), the anchor's read out of the raw palette row its own recover wrote earlier in
// this same chain, this frame — and K = inverse(M_witness_member) . M_witness_anchor.
// ROWS = 4*groupBones; BASE = the group's first appended slot.
struct Vtx { float3 position; float3 normal; float4 tangent; };
StructuredBuffer<Vtx>    q      : register(t0);   // the member's posed vb0, captured at this draw
Buffer<float>            Cpinv  : register(t1);
Buffer<uint>             Map    : register(t2);   // per GROUP bone: this member's local bone, or 0xFFFFFFFF
StructuredBuffer<float4> palRaw : register(t5);   // the RAW palette: the anchor's own recovered rows
RWStructuredBuffer<float4> palOut : register(u1);
static const uint ROWS=%(ROWS)d, BASE=%(BASE)d;
static const uint WITM=%(WITM)d;   // the witness bone's index in THIS member mesh
static const uint WITA=%(WITA)d;   // the anchor-side witness recovery's base row in palRaw
#define POS(i) q[i].position
%(ROWFN)s

" + AffineInverseFn + @"
[numthreads(64,1,1)]
void main(uint3 tid : SV_DispatchThreadID){
    uint i=tid.x; if(i>=ROWS) return;
    uint g=i>>2, comp=i&3;
    uint b=Map[g];
    if(b==0xFFFFFFFF) return;   // this member cannot condition the bone -> leave the row alone
    float4x4 MM=float4x4(Row(WITM,0),Row(WITM,1),Row(WITM,2),Row(WITM,3));
    float4x4 MA=float4x4(palRaw[WITA],palRaw[WITA+1],palRaw[WITA+2],palRaw[WITA+3]);
    float4x4 K=mul(AffineInverse(MM[0],MM[1],MM[2],MM[3]), MA);
    palOut[((BASE+g)<<2)|comp]=mul(Row(b,comp), K);
}
";

    public const string SkinTemplate =
@"// Skin the new body (VCOUNT verts, weighted to UNION bone order) with the CONVERTED palette.
struct Vtx  { float3 position; float3 normal; float4 tangent; };
struct Skin { float4 weight;   uint4  index;  };
// u1 is a stride-zero raw compute buffer. The command list unbinds it after dispatch, then copies
// its bytes into the separate 40-byte vertex buffer used by the draw.
RWByteAddressBuffer     rw_out : register(u1);
StructuredBuffer<Vtx>   bindV  : register(t0);
StructuredBuffer<Skin>  skinB  : register(t1);
// Palette resources are physically structured as 16-byte float4 rows. Keep the shader declaration
// at that exact stride and assemble each 4-row matrix explicitly; rebinding the same resource as a
// StructuredBuffer<Mat> declares a conflicting 64-byte stride and is driver-dependent.
StructuredBuffer<float4> palRows : register(t2);
static const uint VCOUNT=%(VCOUNT)d;
[numthreads(64,1,1)]
void main(uint3 tid : SV_DispatchThreadID){
    uint vid=tid.x; if(vid>=VCOUNT) return;
    Vtx V=bindV[vid]; Skin S=skinB[vid];
    float3 sp=0,sn=0,st=0;
    [unroll] for(int k=0;k<4;k++){
        float wk=S.weight[k]; uint b=S.index[k];
        uint p=b<<2;
        float4x4 M=float4x4(palRows[p],palRows[p+1],palRows[p+2],palRows[p+3]);
        sp+=wk*mul(float4(V.position,1.0),M).xyz;
        sn+=wk*mul(float4(V.normal,0.0),M).xyz;
        st+=wk*mul(float4(V.tangent.xyz,0.0),M).xyz;
    }
    uint o=vid*40;
    rw_out.Store3(o,    asuint(sp));
    rw_out.Store3(o+12, asuint(normalize(sn)));
    rw_out.Store4(o+24, asuint(float4(normalize(st),V.tangent.w)));
}
";

    public const string TieFillTemplate =
@"// The tie underlay for one absent pool part. PAIR rows: a donor-ridden union row the part owns is
// filled with its nearest ANCHOR-owned ancestor's converted row, verbatim — palette rows are combined
// bind->posed affine maps, so a copy IS the rigid ride (the bind-relative delta cancels against the
// ancestor's inverse bind). SEED rows (no path / no anchor-owned ancestor): identity — bind-pose
// placement in the anchor's space. Both are needed because the converts rewrite EVERY union row: an
// absent part's constants-K is zero (its CB was never filled) and witness-K rides an arbitrary bone,
// so a row left to them collapses. Runs in the anchor's chains only while the part's presence latch is
// down; the frame the part returns, its own recover overwrites these rows again.
// Ancestor rows are read from a COPY of the converted palette, not the UAV: a typed UAV load of a
// 4-component format does not compile on cs_5_0 (single-component 32-bit only), which is why every
// compute in this emission reads through a StructuredBuffer and only WRITES its UAV.
StructuredBuffer<float4> palIn  : register(t0);   // the CONVERTED palette, pre-fill
RWStructuredBuffer<float4> palOut : register(u1);
static const uint PAIRS=%(P)d, SEEDS=%(S)d;
static const uint2 PAIR[%(PT)d] = { %(PAIRLIST)s };   // x = tied union slot, y = ancestor union slot
static const uint  SEED[%(ST)d] = { %(SEEDLIST)s };   // union slots reset to identity
[numthreads(64,1,1)]
void main(uint3 tid : SV_DispatchThreadID){
    uint i=tid.x; if(i>=(PAIRS+SEEDS)*4) return;
    uint p=i>>2, comp=i&3;
    if(p<PAIRS){ palOut[(PAIR[p].x<<2)|comp]=palIn[(PAIR[p].y<<2)|comp]; return; }
    palOut[(SEED[p-PAIRS]<<2)|comp]=float4(comp==0?1.0:0.0,comp==1?1.0:0.0,comp==2?1.0:0.0,comp==3?1.0:0.0);
}
";

    static string Lf(string s) => s.Replace("\r\n", "\n");

    /// <summary>Stamp the tie-fill shader: ONE pool part's (tied, ancestor) union-slot pairs and its
    /// identity-seed slots, each in ascending slot order (the derivation's own order, so rebuilds
    /// reproduce). An empty list still stamps a one-element array — HLSL refuses zero-length — whose
    /// count constant keeps every thread off it.</summary>
    public static string EmitTieFill(IReadOnlyList<(uint Tied, uint Ancestor)> pairs, IReadOnlyList<uint> seeds) =>
        Lf(TieFillTemplate)
            .Replace("%(PAIRLIST)s", pairs.Count == 0 ? "uint2(0,0)"
                : string.Join(", ", pairs.Select(p => $"uint2({p.Tied},{p.Ancestor})")))
            .Replace("%(SEEDLIST)s", seeds.Count == 0 ? "0"
                : string.Join(", ", seeds.Select(s => s.ToString())))
            .Replace("%(PT)d", Math.Max(1, pairs.Count).ToString())
            .Replace("%(ST)d", Math.Max(1, seeds.Count).ToString())
            .Replace("%(P)d", pairs.Count.ToString())
            .Replace("%(S)d", seeds.Count.ToString());

    /// <summary>Stamp the SLIM recover shader: dispatch rows ROWS. Per-bone anchor widths are data (the
    /// Off buffer), not compile-time constants.</summary>
    public static string EmitRecover(int rows) =>
        Lf(RecoverTemplate).Replace("%(ROWS)d", rows.ToString());

    /// <summary>Stamp the DENSE recover shader (the whole part ships dense): vertex count N, rows ROWS.</summary>
    public static string EmitRecoverDense(int n, int rows) =>
        Lf(RecoverDenseTemplate).Replace("%(N)d", n.ToString()).Replace("%(ROWS)d", rows.ToString());

    /// <summary>Stamp one convert chunk's shader: the chunk's per-part cbuffer block, owner-indexed
    /// selection block, union row count (4*unionBones). Chunk <paramref name="chunk"/> binds pool parts
    /// [chunk * <see cref="PartsPerConvert"/>, the next chunk's first) at b5 upward, the anchor at b13, and
    /// writes only the rows those parts own. Chunk 0 also keeps the zero fallback for an owner index
    /// outside the pool, so every union row is written by exactly one chunk; a pool that fits one chunk
    /// stamps no chunk guard at all.</summary>
    public static string EmitConvert(int partCount, int unionBones, int chunk = 0)
    {
        var (lo, hi) = ConvertChunkParts(partCount, chunk);
        var cbufs = new StringBuilder();
        for (int pi = lo; pi < hi; pi++)
        {
            if (pi > lo) cbufs.Append('\n');
            cbufs.Append($"cbuffer PartCB{pi} : register(b{PartRegister(pi)}) {{ float4 W{pi}[4]; }}");
        }
        var sel = new StringBuilder();
        if (ConvertChunks(partCount) > 1)
            sel.Append(chunk == 0
                ? $"    if(pi>={hi} && pi<{partCount}) return;   // a later chunk's dispatch converts this row\n"
                : $"    if(pi<{lo} || pi>={hi}) return;   // another chunk's dispatch converts this row\n");
        for (int pi = lo; pi < hi; pi++)
        {
            if (pi > lo) sel.Append('\n');
            sel.Append($"    {(pi == lo ? "if" : "else if")}(pi=={pi}) WP=float4x4(W{pi}[0],W{pi}[1],W{pi}[2],W{pi}[3]);");
        }
        sel.Append("\n    else WP=float4x4(0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0);");
        return Lf(ConvertTemplate)
            .Replace("%(PART_CBUFFERS)s", cbufs.ToString())
            .Replace("%(PART_SELECT)s", sel.ToString())
            .Replace("%(ROWS)d", (4 * unionBones).ToString());
    }

    /// <summary>Stamp the witness convert: union row count, anchor part index, and per part the base
    /// row (slot·4) of its partSide/anchorSide witness recoveries; (~0, ~0) = no witness, rows pass
    /// through unconverted.</summary>
    public static string EmitConvertWitness(int unionBones, int anchorIdx, IReadOnlyList<(uint PartRow, uint AnchorRow)> wit)
    {
        var w = new StringBuilder();
        for (int pi = 0; pi < wit.Count; pi++)
        {
            if (pi > 0) w.Append(", ");
            w.Append($"uint2(0x{wit[pi].PartRow:x8},0x{wit[pi].AnchorRow:x8})");
        }
        return Lf(ConvertWitnessTemplate)
            .Replace("%(ROWS)d", (4 * unionBones).ToString())
            .Replace("%(ANCHOR)d", anchorIdx.ToString())
            .Replace("%(P)d", wit.Count.ToString())
            .Replace("%(WIT)s", w.ToString());
    }

    /// <summary>The row-solve body a fused group shader is stamped with: the ragged SLIM layout when the
    /// member's operator shipped one, the dense all-vertex layout otherwise (<paramref name="n"/> = the
    /// member mesh's vertex count, read only by the dense body).</summary>
    static string GroupRowFn(bool slim, int n) => slim ? GroupRowSlim : GroupRowDense(n);

    /// <summary>The dense body for a mesh of <paramref name="n"/> vertices: whole batches of eight, then the
    /// rest one at a time. The counts are fixed here, so a loop is written only where it would run at least
    /// twice: the compiler warns on a loop it can see runs once or never, and the loader prints that over the
    /// game. A single batch or a single leftover term is written once, in the same place.</summary>
    static string GroupRowDense(int n)
    {
        var s = new StringBuilder()
            .Append($"static const uint N={n};   // the mesh's verts — every row spans all of them\n")
            .Append("float4 Row(uint b, uint comp){\n")
            .Append("    precise float3 a=float3(0,0,0), correction=float3(0,0,0);\n")
            .Append("    uint base=((b<<2)|comp)*N;\n")
            .Append("    uint v=0;\n");
        int batches = n / 8, rest = n % 8;
        if (batches > 0)
            s.Append(batches > 1 ? "    for(;v+8<=N;v+=8){\n" : "    {\n")
                .Append("        float c[8]; float3 p[8];\n")
                .Append("        [unroll] for(uint k0=0;k0<8;k0++){ c[k0]=Cpinv[base+v+k0]; p[k0]=POS((v+k0)); }\n")
                .Append("        [unroll] for(uint k=0;k<8;k++){\n")
                .Append("            precise float3 term=c[k]*p[k];\n")
                .Append(RowAdd).Append("\n        }\n")
                .Append(batches > 1 ? "    }\n" : "        v=8;\n    }\n");
        if (rest > 0)
            s.Append(rest > 1 ? "    for(;v<N;v++){\n" : "    {\n")
                .Append("        precise float3 term=Cpinv[base+v]*POS(v);\n")
                .Append(RowAdd).Append("\n    }\n");
        return s.Append("    return float4(a,(comp==3)?1.0:0.0);\n}").ToString();
    }

    /// <summary>Stamp a group member's LOD0 fused shader: dispatch rows (4·group bones), the group's first
    /// appended palette slot, and the member's operator layout. K is solved from the two constant
    /// buffers.</summary>
    public static string EmitGroupFuse(int groupBones, int slotBase, bool slim, int n) =>
        Lf(GroupFuseTemplate)
            .Replace("%(ROWS)d", (4 * groupBones).ToString())
            .Replace("%(BASE)d", slotBase.ToString())
            .Replace("%(ROWFN)s", Lf(GroupRowFn(slim, n)));

    /// <summary>Stamp a group member's TIER fused shader: as <see cref="EmitGroupFuse"/>, plus the witness
    /// bone's index in this member mesh and the base row of the anchor's own recovery of it.</summary>
    public static string EmitGroupFuseWitness(int groupBones, int slotBase, bool slim, int n,
        int witnessMemberBone, uint witnessAnchorRow) =>
        Lf(GroupFuseWitnessTemplate)
            .Replace("%(ROWS)d", (4 * groupBones).ToString())
            .Replace("%(BASE)d", slotBase.ToString())
            .Replace("%(WITM)d", witnessMemberBone.ToString())
            .Replace("%(WITA)d", witnessAnchorRow.ToString())
            .Replace("%(ROWFN)s", Lf(GroupRowFn(slim, n)));

    /// <summary>Stamp the skin shader with the body vertex count.</summary>
    public static string EmitSkin(int vcount) =>
        Lf(SkinTemplate).Replace("%(VCOUNT)d", vcount.ToString());

    /// <summary>Packet entries per row of a packet texture: entry i sits at pixel
    /// (i % <see cref="PacketWidth"/>, i / <see cref="PacketWidth"/>).</summary>
    public const int PacketWidth = 64;

    /// <summary>The rows of a packet texture holding <paramref name="packet"/> entries.</summary>
    public static int PacketHeight(int packet) => (packet + PacketWidth - 1) / PacketWidth;

    /// <summary>The anchor packet of a mesh of <paramref name="n"/> vertices: the vertices its recover reads,
    /// which the gather draw copies into the packet texture. A slim operator reads the distinct entries
    /// of <paramref name="sel"/>, so its packet is those vertices in ascending order; <c>Sel</c> is
    /// <paramref name="sel"/> with every vertex index replaced by its packet entry. A dense operator
    /// (<paramref name="sel"/> null) reads every vertex: its packet is the whole mesh and it has no
    /// <c>Sel</c>. <c>Index</c> is each packet entry's vertex, the gather's index buffer; <c>Lookup</c>
    /// holds one entry per mesh vertex, its packet entry, and 0 for a vertex outside the packet. A
    /// vertex index at or past <paramref name="n"/>, or a packet with no entry, is refused: neither can be
    /// gathered.</summary>
    public static (uint[] Index, uint[] Lookup, uint[]? Sel) AnchorPacket(uint[]? sel, int n)
    {
        if (n <= 0) throw new ArgumentOutOfRangeException(nameof(n), n, "a packet is gathered from a mesh with vertices");
        if (sel is null)
        {
            var all = new uint[n];
            for (int v = 0; v < n; v++) all[v] = (uint)v;
            return (all, (uint[])all.Clone(), null);
        }
        if (sel.Length == 0) throw new ArgumentException("a slim operator that reads no vertex has no packet", nameof(sel));
        foreach (uint v in sel)
            if (v >= (uint)n)
                throw new ArgumentOutOfRangeException(nameof(sel), v, $"a {n}-vertex mesh has no vertex {v}");
        var index = sel.Distinct().OrderBy(v => v).ToArray();
        var lookup = new uint[n];
        for (int i = 0; i < index.Length; i++) lookup[index[i]] = (uint)i;
        return (index, lookup, sel.Select(v => lookup[v]).ToArray());
    }

    public const string GatherVertexTemplate =
@"// The anchor-packet gather of one mesh: one point per packet entry. The index buffer names each entry's
// vertex, so the input assembler hands this shader that vertex's posed position from the draw's own
// vertex buffer; the lookup places it at its entry's pixel of the packet texture.
Buffer<uint> lookup : register(t1);   // per mesh vertex: its packet entry
struct In  { float3 pos : POSITION; uint id : SV_VertexID; };
struct Out { float4 pos : SV_Position; nointerpolation float3 p : TEXCOORD0; };
Out main(In v){ Out o; uint i = lookup[v.id]; uint x = i % %(PW)d, y = i / %(PW)d;
    o.pos = float4((x + 0.5) / %(PW)d.0 * 2 - 1, 1 - (y + 0.5) / %(H)d.0 * 2, 0.5, 1); o.p = v.pos; return o; }
";

    /// <summary>The gather's pixel shader, one per mod: it writes the position it is handed, unrounded.</summary>
    public const string GatherPixelShader =
@"// Writes each gathered position into the packet texture as it arrives: no interpolation, no rounding.
float4 main(float4 pos : SV_Position, nointerpolation float3 p : TEXCOORD0) : SV_Target { return float4(p, 1); }
";

    /// <summary>Stamp one mesh's gather vertex shader for a packet of <paramref name="packet"/> entries.</summary>
    public static string EmitGather(int packet) =>
        Lf(GatherVertexTemplate).Replace("%(PW)d", PacketWidth.ToString())
            .Replace("%(H)d", PacketHeight(packet).ToString());

    /// <summary>The gather's pixel shader, as shipped.</summary>
    public static string EmitGatherPixel() => Lf(GatherPixelShader);

    // ---- the per-copy pose route: pixel passes in place of the cache kernel -------------------------
    // Every pass is a full-viewport triangle; the pixel shader does the work. No compute runs between the
    // game's draws and nothing it writes is consumed through an unordered-access view: in the game, a
    // draw that follows an unordered-access write waits for the pipeline to drain, and that wait was the
    // whole cost of the kernel route.

    /// <summary>The one vertex shader every pose pass draws with: a triangle covering the viewport.</summary>
    public const string PoseFullscreenShader =
@"// One triangle over the whole viewport; the pixel shader of each pose pass does the work per pixel.
float4 main(uint id : SV_VertexID) : SV_Position {
    return float4(float2((id << 1) & 2, id & 2) * float2(2, -2) + float2(-1, 1), 0.5, 1);
}
";

    public static string EmitPoseFullscreen() => Lf(PoseFullscreenShader);

    /// <summary>The palette pass of one kernel mesh: one pixel per palette row, recovered from the packet
    /// the gather just filled with the row solve the kernel used. Palette slot u, component comp, at pixel
    /// 4u + comp. A slot no local bone maps to keeps the identity row; a tied slot (PAIR) takes its source
    /// slot's row, as the kernel's step 5 copied it.</summary>
    public const string PosePaletteTemplate =
@"// The pose palette of one replaced mesh at one of its draws: one pixel per palette row (slot u,
// component comp at pixel 4u+comp), each the compensated row solve over the packet the gather just read
// out of this draw's own vertex buffer. Written as a render target, read by the skin pass of the same draw.
Texture2D<float4>       q      : register(t0);   // this draw's packet: entry i at pixel (i % %(PW)d, i / %(PW)d)
float3 Q(uint i){ return q.Load(int3(i % %(PW)d, i / %(PW)d, 0)).xyz; }
Buffer<float>           Cpinv  : register(t1);
Buffer<uint>            Map    : register(t2);   // local bone -> palette slot, or 0xFFFFFFFF
static const uint ROWS=%(ROWS)d;                 // 4 per local bone this mesh recovers
%(PAIRDECL)s#define POS(i) Q(i)
%(ROWFN)s
float4 main(float4 pos : SV_Position) : SV_Target {
    uint p = (uint)pos.x;
    uint u = p >> 2, comp = p & 3;
%(TIES)s%(SEARCH)s    return float4(comp == 0 ? 1.0 : 0.0, comp == 1 ? 1.0 : 0.0, comp == 2 ? 1.0 : 0.0, comp == 3 ? 1.0 : 0.0);
}
";

    /// <summary>Stamp one kernel mesh's palette pass: its row count (4 per recovered bone), its operator
    /// layout (slim reads the remapped vertex list; dense reads all <paramref name="n"/> vertices) and its
    /// tie pairs (tied slot, source slot). Counts the build already knows are written out, not looped
    /// over where the loop would run once: the compiler warns on such a loop and the runtime prints the
    /// warning over the game.</summary>
    public static string EmitPosePalette(int rows, bool slim, int n, IReadOnlyList<(uint Tied, uint Source)> pairs,
        IReadOnlyList<bool>? sound = null)
    {
        if (rows <= 0 || rows % 4 != 0) throw new ArgumentOutOfRangeException(nameof(rows), rows, "four rows per recovered bone");
        int bones = rows / 4;
        if (sound is not null)
        {
            // the pooled route's kernel pass: the same rows, and the row mask beside them
            if (sound.Count != bones) throw new ArgumentException("one soundness flag per recovered bone", nameof(sound));
            string own = "u == asked ? SOUND[{0}] : 0.0";
            string masked = bones == 1
                ? $"    if (Map[0] == u) return Write(Row(0, comp), {string.Format(own, "0")});\n"
                : "    [loop] for (uint b = 0; b < ROWS / 4; b++) {\n"
                  + $"        if (Map[b] == u) return Write(Row(b, comp), {string.Format(own, "b")});\n    }}\n";
            return Lf(PoseMaskPaletteTemplate)
                .Replace("%(PW)d", PacketWidth.ToString())
                .Replace("%(ROWS)d", rows.ToString())
                .Replace("%(BONES)d", bones.ToString())
                .Replace("%(SOUND)s", SoundList(sound))
                .Replace("%(PAIRDECL)s", PairDecl(pairs))
                .Replace("%(TIES)s", PairRedirects(pairs))
                .Replace("%(SEARCH)s", masked)
                .Replace("%(ROWFN)s", Lf(GroupRowFn(slim, n)));
        }
        string search = bones == 1
            ? "    if (Map[0] == u) return Row(0, comp);\n"
            : "    [loop] for (uint b = 0; b < ROWS / 4; b++) {\n        if (Map[b] == u) return Row(b, comp);\n    }\n";
        return Lf(PosePaletteTemplate)
            .Replace("%(PW)d", PacketWidth.ToString())
            .Replace("%(ROWS)d", rows.ToString())
            .Replace("%(PAIRDECL)s", PairDecl(pairs))
            .Replace("%(TIES)s", PairRedirects(pairs))
            .Replace("%(SEARCH)s", search)
            .Replace("%(ROWFN)s", Lf(GroupRowFn(slim, n)));
    }

    /// <summary>The palette pass of one kernel mesh of a replacement that takes rows from other parts:
    /// <see cref="PosePaletteTemplate"/>'s rows, and beside them the row mask, which takes 1 at every pixel
    /// of a slot whose row is this mesh's own recovery of the slot's bone (<c>SOUND</c>) and 0 at a tie, an
    /// identity row and a slot this mesh does not recover. A part placed by a bone's row reads the mask at
    /// that bone's slot.</summary>
    public const string PoseMaskPaletteTemplate =
@"// The pose palette of one replaced mesh at one of its draws: one pixel per palette row (slot u,
// component comp at pixel 4u+comp), each the compensated row solve over the packet the gather just read
// out of this draw's own vertex buffer. The mask (second target) holds 1 at every pixel of a slot whose
// row is this mesh's own recovery of that slot's bone, and 0 at a tie, an identity row and a slot this
// mesh does not recover; a part placed by a bone's row reads it at that bone's slot.
Texture2D<float4>       q      : register(t0);   // this draw's packet: entry i at pixel (i % %(PW)d, i / %(PW)d)
float3 Q(uint i){ return q.Load(int3(i % %(PW)d, i / %(PW)d, 0)).xyz; }
Buffer<float>           Cpinv  : register(t1);
Buffer<uint>            Map    : register(t2);   // local bone -> palette slot, or 0xFFFFFFFF
static const uint ROWS=%(ROWS)d;                 // 4 per local bone this mesh recovers
static const float SOUND[%(BONES)d] = { %(SOUND)s };   // per local bone: 1 where its row is its own recovery
%(PAIRDECL)s#define POS(i) Q(i)
%(ROWFN)s
struct PaletteOut { float4 row : SV_Target0; float4 own : SV_Target1; };
PaletteOut Write(float4 row, float own){ PaletteOut o; o.row = row; o.own = float4(own, 0, 0, 0); return o; }
PaletteOut main(float4 pos : SV_Position) {
    uint p = (uint)pos.x;
    uint u = p >> 2, comp = p & 3;
    uint asked = u;
%(TIES)s%(SEARCH)s    return Write(float4(comp == 0 ? 1.0 : 0.0, comp == 1 ? 1.0 : 0.0, comp == 2 ? 1.0 : 0.0, comp == 3 ? 1.0 : 0.0), 0.0);
}
";

    /// <summary>The skin pass of one piece: one pixel per float4 element of the piece's flat 40-byte
    /// stream. The element's four floats belong to one or two local vertices; each maps to a donor vertex,
    /// skinned against the palette texture with the chain skin's four-bone body (<see cref="SkinTemplate"/>:
    /// a change to one belongs in both).</summary>
    public const string PoseSkinTemplate =
@"// The posed stream of one piece of the replacement, at one draw: one pixel per float4 element of the
// flat stream (position, normal, tangent: 40 bytes a vertex, so an element straddles two vertices every
// fifth element). Each local vertex maps to a donor vertex, skinned against the palette the palette pass
// just wrote; the buffer this writes is the draw's stream 0 through a stride-40 alias.
struct Vtx  { float3 position; float3 normal; float4 tangent; };
struct Skin { float4 weight;   uint4  index;  };
Texture2D<float4>       pal    : register(t0);   // palette row r at pixel r
Buffer<uint>            map    : register(t1);   // this piece: local vertex -> donor vertex
StructuredBuffer<Vtx>   bindV  : register(t5);   // the replacement's bind geometry
StructuredBuffer<Skin>  skinB  : register(t6);   // its weights, in palette slot order
static const uint VCOUNT=%(VCOUNT)d;
void SkinVertex(uint vid, out float v[10]) {
    Vtx V=bindV[vid]; Skin S=skinB[vid];
    float3 sp=0,sn=0,st=0;
    [unroll] for(int k=0;k<4;k++){
        float wk=S.weight[k]; uint b=S.index[k];
        uint p=b<<2;
        float4x4 M=float4x4(pal.Load(int3(p,0,0)),pal.Load(int3(p+1,0,0)),pal.Load(int3(p+2,0,0)),pal.Load(int3(p+3,0,0)));
        sp+=wk*mul(float4(V.position,1.0),M).xyz;
        sn+=wk*mul(float4(V.normal,0.0),M).xyz;
        st+=wk*mul(float4(V.tangent.xyz,0.0),M).xyz;
    }
    sn=normalize(sn); st=normalize(st);
    v[0]=sp.x; v[1]=sp.y; v[2]=sp.z; v[3]=sn.x; v[4]=sn.y; v[5]=sn.z; v[6]=st.x; v[7]=st.y; v[8]=st.z; v[9]=V.tangent.w;
}
float4 main(float4 pos : SV_Position) : SV_Target {
    uint g0 = (uint)pos.x * 4;             // the element's first float in the flat stream
    uint v0 = g0 / 10, v1 = (g0 + 3) / 10;  // the local vertices it covers
    float a[10], b[10];
    SkinVertex(min(map[v0], VCOUNT - 1), a);
    if (v1 != v0) SkinVertex(min(map[v1], VCOUNT - 1), b); else b = a;
    float4 o;
    [unroll] for (uint k = 0; k < 4; k++) {
        uint g = g0 + k;
        o[k] = (g / 10 == v0) ? a[g % 10] : b[g % 10];
    }
    return o;
}
";

    /// <summary>Stamp the skin pass with the replacement's vertex count.</summary>
    public static string EmitPoseSkin(int vcount) =>
        Lf(PoseSkinTemplate).Replace("%(VCOUNT)d", vcount.ToString());

    public const string TierTieFillTemplate =
@"// The tier tie for one LOD level of a pipeline. Each PAIR row is a donor-weighted union row that the
// tier chain at this level writes NOTHING for: the level's mesh does not rig the bone (or its recovery
// there sentinelled to lod0), so the row would otherwise stand at whatever the last lod0-tier frame
// recovered, or at the identity seed. It is filled with a verbatim copy of a row this level DOES write,
// the co-riding bone chosen at build time — palette rows are combined bind->posed affines, so a copy is
// the rigid ride. Runs after the witness convert and before the skin in this level's chain only.
// Source rows are read from a COPY of the converted palette, never the UAV (cs_5_0 has no
// 4-component typed UAV load).
StructuredBuffer<float4> palIn  : register(t0);   // the CONVERTED palette, pre-fill
RWStructuredBuffer<float4> palOut : register(u1);
static const uint PAIRS=%(P)d;
static const uint2 PAIR[%(PT)d] = { %(PAIRLIST)s };   // x = tied union slot, y = source union slot
[numthreads(64,1,1)]
void main(uint3 tid : SV_DispatchThreadID){
    uint i=tid.x; if(i>=PAIRS*4) return;
    uint p=i>>2, comp=i&3;
    palOut[(PAIR[p].x<<2)|comp]=palIn[(PAIR[p].y<<2)|comp];
}
";

    /// <summary>Stamp one LOD level's tier-tie shader: (tied, source) union-slot pairs in ascending tied-slot
    /// order. Never stamped empty — a level with no orphan row gets no shader and no run.</summary>
    public static string EmitTierTieFill(IReadOnlyList<(uint Tied, uint Source)> pairs)
    {
        if (pairs.Count == 0) throw new ArgumentException("a tier tie needs at least one pair", nameof(pairs));
        return Lf(TierTieFillTemplate)
            .Replace("%(PAIRLIST)s", string.Join(", ", pairs.Select(p => $"uint2({p.Tied},{p.Source})")))
            .Replace("%(PT)d", pairs.Count.ToString())
            .Replace("%(P)d", pairs.Count.ToString());
    }

    /// <summary>A palette pass's tie pairs as constants: (tied palette slot, the slot whose row it takes).</summary>
    static string PairDecl(IReadOnlyList<(uint Tied, uint Source)> pairs) =>
        pairs.Count == 0 ? "" : $"static const uint PAIRS={pairs.Count};\n"
            + $"static const uint2 PAIR[{pairs.Count}] = {{ "
            + string.Join(", ", pairs.Select(p => $"uint2({p.Tied},{p.Source})"))
            + " };   // x = tied palette slot, y = its source slot\n";

    /// <summary>The redirects a palette pass applies before its search, in pair order, so a pair whose source
    /// slot is itself tied by a later pair follows both.</summary>
    static string PairRedirects(IReadOnlyList<(uint Tied, uint Source)> pairs) =>
        pairs.Count == 0 ? "" :
            "    // a row this mesh does not pose rides the co-riding bone chosen at build time\n"
            + string.Concat(Enumerable.Range(0, pairs.Count).Select(i => $"    if (PAIR[{i}].x == u) u = PAIR[{i}].y;\n"));

    // ---- the pooled pose route: copies of the parts a replacement takes rows from, told apart by placement
    // Each mesh a replacement takes rows from keeps a ring of slots, filled in turn, one per draw of the
    // mesh: at every draw its gather writes its packet straight into the next slot, and a stamp pass writes
    // the draw's object-to-world rows and the frame number beside it. Neither reads anything rendered this
    // frame, so a ring write waits on no pass. At the replacement's draw the mesh's palette pass chooses this
    // copy's slot by where it stands, counting the slots one copy filled from several passes as one, and
    // solves and rebases its rows straight out of that slot's packet; a mesh placed by a bone's row has that
    // slot chosen by a pick pass first, which reads the palette so far. The frame number is a texture the
    // [Present] pass advances.

    /// <summary>The object-to-world capture's vertex shader: a triangle over the viewport that hands the
    /// four rows of the draw's own vertex-shader constant buffer b1 to every pixel. Runs with b1 exactly as
    /// the game bound it for the draw, so the section running it binds no constant buffer.</summary>
    public const string PoseCaptureVertexShader =
@"cbuffer DrawConstants:register(b1) {float4 matrixRows[4];};
struct Output {float4 pos:SV_Position; nointerpolation float4 r0:TEXCOORD0; nointerpolation float4 r1:TEXCOORD1; nointerpolation float4 r2:TEXCOORD2; nointerpolation float4 r3:TEXCOORD3;};
Output main(uint id:SV_VertexID){Output o;float2 p=float2((id<<1)&2,id&2);o.pos=float4(p*float2(2,-2)+float2(-1,1),0,1);o.r0=matrixRows[0];o.r1=matrixRows[1];o.r2=matrixRows[2];o.r3=matrixRows[3];return o;}
";

    /// <summary>The replaced part's capture: pixels 0 to 3 of its 5x1 target take the draw's four rows,
    /// pixel 4 this frame's number out of the frame texture.</summary>
    public const string PoseCapturePixelShader =
@"Texture2D<float4> frame:register(t0);   // pixel 0: this frame's number
float4 main(float4 pos:SV_Position,nointerpolation float4 r0:TEXCOORD0,nointerpolation float4 r1:TEXCOORD1,nointerpolation float4 r2:TEXCOORD2,nointerpolation float4 r3:TEXCOORD3):SV_Target {uint x=(uint)pos.x;return x==0?r0:x==1?r1:x==2?r2:x==3?r3:float4(frame.Load(int3(0,0,0)).x,0,0,0);}
";

    public static string EmitPoseCaptureVertex() => Lf(PoseCaptureVertexShader);
    public static string EmitPoseCapturePixel() => Lf(PoseCapturePixelShader);

    /// <summary>The frame pass: this frame's number plus one, back to 1 at the wrap, into the texture the
    /// <c>[Present]</c> command list copies back over the frame texture.</summary>
    public const string PoseFrameTemplate =
@"// The next frame's number: this frame's plus one, back to 1 at %(WRAP)d so it stays an exact float.
// The ring slots and the replaced part's capture are stamped with it, and a slot no draw has written
// holds 0, which no frame number equals.
Texture2D<float4> frame : register(t0);   // pixel 0: this frame's number
float4 main(float4 pos : SV_Position) : SV_Target {
    float next = frame.Load(int3(0, 0, 0)).x + 1;
    return float4(next >= %(WRAP)d.0 ? 1.0 : next, 0, 0, 0);
}
";

    /// <summary>Stamp the frame pass with the number it wraps at.</summary>
    public static string EmitPoseFrame(int wrap) => Lf(PoseFrameTemplate).Replace("%(WRAP)d", wrap.ToString());

    /// <summary>The ring gather at one draw of a source mesh: the plain gather (<see cref="GatherVertexTemplate"/>)
    /// placed in the slot this draw fills, whose number the ring block binds at t2: one point per packet entry
    /// into rows slot·S to slot·S+H−1 of the ring texture. Everything it reads is the draw's own vertex buffer
    /// or a file the mod loads, so it waits on no pass.</summary>
    public const string RingGatherTemplate =
@"// The gather of one source mesh into its ring: one point per packet entry, placed in the slot this draw
// fills. The index buffer names each entry's vertex, so the input assembler hands this shader that vertex's
// posed position out of the draw's own vertex buffer; the lookup gives its entry, the slot number its slot.
// Slot k: its packet at rows k*S to k*S+H-1, its rows and frame number in row k*S+H.
Buffer<uint> lookup : register(t1);   // per mesh vertex: its packet entry
Buffer<uint> slot   : register(t2);   // element 0: the ring slot this draw fills
struct In  { float3 pos : POSITION; uint id : SV_VertexID; };
struct Out { float4 pos : SV_Position; nointerpolation float3 p : TEXCOORD0; };
Out main(In v){ Out o; uint i = lookup[v.id]; uint x = i % %(PW)d, y = slot[0] * %(S)d + i / %(PW)d;
    o.pos = float4((x + 0.5) / %(PW)d.0 * 2 - 1, 1 - (y + 0.5) / %(T)d.0 * 2, 0.5, 1); o.p = v.pos; return o; }
";

    /// <summary>Stamp one source mesh's ring gather for a packet of <paramref name="packet"/> entries and a
    /// ring of <paramref name="ringEntries"/> slots.</summary>
    public static string EmitRingGather(int packet, int ringEntries)
    {
        if (ringEntries <= 0) throw new ArgumentOutOfRangeException(nameof(ringEntries), ringEntries, "a ring holds slots");
        return Lf(RingGatherTemplate).Replace("%(PW)d", PacketWidth.ToString())
            .Replace("%(S)d", RingSlotRows(packet).ToString())
            .Replace("%(T)d", (RingSlotRows(packet) * ringEntries).ToString());
    }

    /// <summary>The ring stamp at one draw of a source mesh: the draw's four object-to-world rows (from the
    /// capture vertex shader, <see cref="PoseCaptureVertexShader"/>) at pixels 0 to 3 of the last row of the
    /// slot this draw fills, and this frame's number at pixel 4. Every other pixel of the ring is left as it
    /// is. Like the gather, it reads nothing rendered this frame.</summary>
    public const string RingStampTemplate =
@"// One draw of a source mesh stamps the ring slot its gather just filled: the draw's object-to-world rows at
// pixels 0-3 of the slot's last row (k*S+H), this frame's number at pixel 4. Every other pixel keeps what it
// holds.
Texture2D<float4> frame : register(t0);   // pixel 0: this frame's number
Buffer<uint>      slot  : register(t2);   // element 0: the ring slot this draw fills
static const uint H=%(H)d;               // rows of one packet
static const uint S=H+1;                 // rows of one slot
float4 main(float4 pos:SV_Position,nointerpolation float4 r0:TEXCOORD0,nointerpolation float4 r1:TEXCOORD1,nointerpolation float4 r2:TEXCOORD2,nointerpolation float4 r3:TEXCOORD3) : SV_Target {
    uint x = (uint)pos.x, y = (uint)pos.y;
    if (y != slot[0] * S + H || x > 4) discard;
    return x == 0 ? r0 : x == 1 ? r1 : x == 2 ? r2 : x == 3 ? r3 : float4(frame.Load(int3(0, 0, 0)).x, 0, 0, 0);
}
";

    /// <summary>Stamp one source mesh's ring stamp for a packet of <paramref name="packet"/> entries.</summary>
    public static string EmitRingStamp(int packet) =>
        Lf(RingStampTemplate).Replace("%(H)d", PacketHeight(packet).ToString());

    /// <summary>Rows one ring slot spans: its packet's rows and the row holding its object-to-world rows and
    /// frame number.</summary>
    public static int RingSlotRows(int packet) => PacketHeight(packet) + 1;

    /// <summary>The palette pass of one mesh of a part the replacement takes rows from: the row solve of
    /// <see cref="PosePaletteTemplate"/> over the packet region of the ring slot this draw's copy owns, each
    /// row rebased into the replaced part's space. The slot is chosen in the pass itself by the mesh's pick
    /// rule, or, for a mesh a bone's row places, read from its pick pass. A slot the mesh does not pose, and
    /// every slot when no copy is found, is discarded, so the replaced part's own pass keeps what it wrote
    /// there. Its second target, the row mask, takes 1 where the row is this mesh's own recovery of the
    /// slot's bone (<c>SOUND</c>) and 0 where a tie redirected the slot to another bone's row.</summary>
    public const string PosePoolPaletteTemplate =
@"// The rows one part gives the replacement at one draw, from the copy of that part this draw belongs
// with: the compensated row solve over that copy's packet in the part's ring (slot u, component comp at
// pixel 4u+comp), each row rebased from that copy's object space into the replaced part's:
// row . K, K = W_part . inverse(W_replaced). A slot this mesh does not pose, and every slot when no copy
// is found, keeps what the replaced part's palette pass wrote there. The mask (second target) says which
// written rows are this mesh's own recovery of their slot's bone.
Texture2D<float4>       q      : register(t0);   // the part's ring: slot s's entry i at pixel (i % %(PW)d, s*S + i / %(PW)d), its rows at pixels 0-3 of row s*S+H, its frame at pixel 4
static const uint H=%(H)d;                       // rows of one packet
static const uint S=H+1;                         // rows of one ring slot
static uint Slot;                                // the ring slot of this draw's copy, chosen before any row is solved
float3 Q(uint i){ return q.Load(int3(i % %(PW)d, Slot * S + i / %(PW)d, 0)).xyz; }
Buffer<float>           Cpinv  : register(t1);
Buffer<uint>            Map    : register(t2);   // local bone -> palette slot, or 0xFFFFFFFF
%(PLACEDECL)sTexture2D<float4>       anc    : register(t6);   // the replaced part's object-to-world rows at this draw, then its frame number
static const uint ROWS=%(ROWS)d;                 // 4 per local bone this mesh recovers
static const float SOUND[%(BONES)d] = { %(SOUND)s };   // per local bone: 1 where its row is its own recovery
%(PAIRDECL)s#define POS(i) Q(i)
%(ROWFN)s
%(AFFINV)s
struct PaletteOut { float4 row : SV_Target0; float4 own : SV_Target1; };
PaletteOut Write(float4 row, float own){ PaletteOut o; o.row = row; o.own = float4(own, 0, 0, 0); return o; }
PaletteOut main(float4 pos : SV_Position) {
    float4 A0 = anc.Load(int3(0, 0, 0)), A1 = anc.Load(int3(1, 0, 0)), A2 = anc.Load(int3(2, 0, 0)), A3 = anc.Load(int3(3, 0, 0));
%(PLACE)s    uint p = (uint)pos.x;
    uint u = p >> 2, comp = p & 3;
    uint asked = u;
%(TIES)s    float4x4 K = mul(W, AffineInverse(A0, A1, A2, A3));
%(SEARCH)s    discard;
    return Write(float4(0, 0, 0, 0), 0.0);
}
";

    /// <summary>Stamp one source mesh's palette pass on the pooled route: its row count, operator layout and
    /// tie pairs as <see cref="EmitPosePalette"/> takes them, per local bone whether its row is its own
    /// recovery (<paramref name="sound"/>: false for a bone the operator ties to another), its packet of
    /// <paramref name="packet"/> entries, and how this draw's copy is found: by <paramref name="rule"/> in the
    /// pass itself over a ring of <paramref name="ringEntries"/> slots with the pick's margins, reading the
    /// slots' rows out of the ring texture it reads the packets from, or, for <see cref="PickRule.ByRow"/>,
    /// as the mesh's pick pass found it.</summary>
    public static string EmitPoolPosePalette(int rows, bool slim, int n, IReadOnlyList<(uint Tied, uint Source)> pairs,
        IReadOnlyList<bool> sound, int packet, PickRule rule, int ringEntries, float cap, float ahead, float same, float dup)
    {
        if (rows <= 0 || rows % 4 != 0) throw new ArgumentOutOfRangeException(nameof(rows), rows, "four rows per recovered bone");
        if (sound.Count != rows / 4) throw new ArgumentException("one soundness flag per recovered bone", nameof(sound));
        int bones = rows / 4;
        string own = "u == asked ? SOUND[{0}] : 0.0";
        string search = bones == 1
            ? $"    if (Map[0] == u) return Write(mul(Row(0, comp), K), {string.Format(own, "0")});\n"
            : "    [loop] for (uint b = 0; b < ROWS / 4; b++) {\n"
              + $"        if (Map[b] == u) return Write(mul(Row(b, comp), K), {string.Format(own, "b")});\n    }}\n";
        bool picked = rule == PickRule.ByRow;
        string placeDecl = picked
            ? "Texture2D<float4>       pick   : register(t5);   // pixel 0: the chosen slot, negative for none; pixels 1-4: its rows\n"
            : PickFunction(rule, ringEntries, cap, ahead, same, dup, ringTex: "q", packet: packet);
        string place = picked
            ? "    // the copy the pick pass found by where a bone's row places it\n"
              + "    float4 chosen = pick.Load(int3(0, 0, 0));\n"
              + "    if (chosen.x < 0) discard;\n"
              + "    Slot = (uint)chosen.x;\n"
              + "    float4x4 W = float4x4(pick.Load(int3(1, 0, 0)), pick.Load(int3(2, 0, 0)), pick.Load(int3(3, 0, 0)), pick.Load(int3(4, 0, 0)));\n"
            : "    // the copy standing where this copy of the replaced part places it\n"
              + "    int chosen = PickSlot(A0, A1, A2, A3, anc.Load(int3(4, 0, 0)).x);\n"
              + "    if (chosen < 0) discard;\n"
              + "    Slot = (uint)chosen;\n"
              + "    float4x4 W = float4x4(Ring(Slot, 0), Ring(Slot, 1), Ring(Slot, 2), Ring(Slot, 3));\n";
        return Lf(PosePoolPaletteTemplate)
            .Replace("%(PLACEDECL)s", placeDecl)
            .Replace("%(PLACE)s", place)
            .Replace("%(PW)d", PacketWidth.ToString())
            .Replace("%(H)d", PacketHeight(packet).ToString())
            .Replace("%(ROWS)d", rows.ToString())
            .Replace("%(BONES)d", bones.ToString())
            .Replace("%(SOUND)s", SoundList(sound))
            .Replace("%(PAIRDECL)s", PairDecl(pairs))
            .Replace("%(TIES)s", PairRedirects(pairs))
            .Replace("%(SEARCH)s", search)
            .Replace("%(ROWFN)s", Lf(GroupRowFn(slim, n)))
            .Replace("%(AFFINV)s", Lf(AffineInverseFn));
    }

    /// <summary>Per local bone, 1.0 where its row is its own recovery and 0.0 where the operator ties it.</summary>
    static string SoundList(IReadOnlyList<bool> sound) => string.Join(", ", sound.Select(s => s ? "1.0" : "0.0"));

    /// <summary>How a pick predicts where this copy's slot stands.</summary>
    public enum PickRule
    {
        /// <summary>The part's root is the replaced part's own: the slot binds the replaced part's
        /// object-to-world rows.</summary>
        SameRoot,
        /// <summary>The root's rest origin, carried by a palette row already written at this draw.</summary>
        ByRow,
        /// <summary>The replaced part's own position.</summary>
        ByPosition,
    }

    /// <summary>The pick rule as one HLSL function, which the pick pass and the palette pass that picks for
    /// itself both include: <c>PickSlot</c> returns the ring slot this draw's copy owns among those written
    /// in its frame, or -1 when none stands near enough or two copies stand equally near. Slots holding the
    /// same rows within <c>DUP</c> are one copy drawn in several passes and count once. It reads the ring's
    /// rows through <c>Ring</c>, out of the ring texture the including shader declares.</summary>
    const string PickFunctionTemplate =
@"static const uint RING=%(N)d;           // ring slots
static const float CAP=%(CAP)s;         // farthest a slot may stand from where this copy places it
static const float AHEAD=%(AHEAD)s;     // the nearest copy must be this many times nearer than the next
static const float SAME=%(SAME)s;       // per element: a slot binding the replaced part's own rows
static const float DUP=%(DUP)s;         // per element: two slots holding one copy, written from two passes
float4 Ring(uint k, uint r){ return %(RINGTEX)s.Load(int3(r, k * %(S)d + %(H)d, 0)); }
bool Live(uint k, float stamp){ return Ring(k,4).x==stamp; }
bool SameCopy(uint a, uint b){
    return all(abs(Ring(a,0)-Ring(b,0))<=DUP) && all(abs(Ring(a,1)-Ring(b,1))<=DUP)
        && all(abs(Ring(a,2)-Ring(b,2))<=DUP) && all(abs(Ring(a,3)-Ring(b,3))<=DUP);
}
// Which copy of one part this draw of the replacement belongs with: among the ring slots the part's own
// draws wrote in this draw's frame (one per draw, so a copy drawn in several passes holds several), the
// slot standing where this copy places the part's root, near enough and clearly nearer than any other
// copy; -1 for none. A0-A3 are the replaced part's object-to-world rows at this draw, stamp its frame number.
int PickSlot(float4 A0, float4 A1, float4 A2, float4 A3, float stamp){
    int pick=-1;
%(CHOOSE)s    return pick;
}
";

    /// <summary>The pick pass of one source mesh a bone's row places: which of its ring slots this draw's
    /// copy owns, and that slot's object-to-world rows. Pixel 0 holds the slot, or -1 when no slot written
    /// this frame is near enough, two stand equally near, or the placing row is not this draw's own
    /// recovery of its bone; pixels 1 to 4 the chosen slot's rows.</summary>
    public const string PosePickTemplate =
@"// Which copy of one part this draw of the replacement belongs with, chosen by where a bone's row of this
// draw's palette places the part's root. Pixel 0 holds the slot, or -1 for none; pixels 1-4 the chosen
// slot's object-to-world rows.
Texture2D<float4> pal  : register(t0);   // this draw's palette so far: slot u's rows at pixels 4u..4u+3
Texture2D<float4> anc  : register(t1);   // the replaced part's object-to-world rows, then its frame number
%(MASKDECL)sTexture2D<float4> ring : register(t3);   // the part's ring: slot k's rows at pixels 0-3 of row k*S+H, its frame at pixel 4
%(PICKFN)sfloat4 main(float4 pos : SV_Position) : SV_Target {
    uint x=(uint)pos.x;
    int pick=PickSlot(anc.Load(int3(0,0,0)), anc.Load(int3(1,0,0)), anc.Load(int3(2,0,0)), anc.Load(int3(3,0,0)),
        anc.Load(int3(4,0,0)).x);
%(MASK)s    if(x==0) return float4((float)pick,0,0,0);
    if(pick<0) return float4(0,0,0,0);
    return Ring((uint)pick, x-1);
}
";

    const string PickSameRoot =
@"    // the part shares the replaced part's root: this copy's slot binds the replaced part's own rows
    [unroll] for(uint s=0;s<RING;s++)
        if(pick<0 && Live(s,stamp) && all(abs(Ring(s,0)-A0)<=SAME) && all(abs(Ring(s,1)-A1)<=SAME)
           && all(abs(Ring(s,2)-A2)<=SAME) && all(abs(Ring(s,3)-A3)<=SAME)) pick=(int)s;
";

    const string PickNearest =
@"    float3 want=%(WANT)s;
    float best=1e30;
    int nearest=-1;
    [unroll] for(uint c=0;c<RING;c++){
        if(!Live(c,stamp)) continue;
        float d=distance(Ring(c,3).xyz, want);
        if(d<best){ best=d; nearest=(int)c; }
    }
    if(nearest>=0 && best<=CAP){
        // the nearest other copy; a slot holding the nearest's own rows is that copy from another pass
        float next=1e30;
        [unroll] for(uint o=0;o<RING;o++){
            if(o==(uint)nearest || !Live(o,stamp) || SameCopy(o,(uint)nearest)) continue;
            next=min(next, distance(Ring(o,3).xyz, want));
        }
        if(next>AHEAD*best) pick=nearest;
    }
";

    /// <summary>Stamp the pick rule's function (<see cref="PickFunctionTemplate"/>): <paramref name="rule"/>
    /// over a ring of <paramref name="ringEntries"/> slots with the pick's margins. <see cref="PickRule.ByRow"/>
    /// carries <paramref name="rest"/> (the root's rest origin in bind space) by palette slot
    /// <paramref name="slot"/>'s rows, read from a texture named <c>pal</c>, and then the replaced part's
    /// rows. The ring's rows are read out of the texture named <paramref name="ringTex"/>, whose slots span
    /// <see cref="RingSlotRows"/> rows for a packet of <paramref name="packet"/> entries.</summary>
    static string PickFunction(PickRule rule, int ringEntries, float cap, float ahead, float same, float dup,
        string ringTex, int packet, uint slot = 0, (float X, float Y, float Z) rest = default)
    {
        if (ringEntries <= 0) throw new ArgumentOutOfRangeException(nameof(ringEntries), ringEntries, "a ring holds slots");
        string choose = rule switch
        {
            PickRule.SameRoot => PickSameRoot,
            PickRule.ByRow => PickNearest.Replace("%(WANT)s",
                $"mul(mul(float4({Hf(rest.X)},{Hf(rest.Y)},{Hf(rest.Z)},1), float4x4(pal.Load(int3({4 * slot},0,0)),"
                + $"pal.Load(int3({4 * slot + 1},0,0)),pal.Load(int3({4 * slot + 2},0,0)),pal.Load(int3({4 * slot + 3},0,0)))),"
                + " float4x4(A0,A1,A2,A3)).xyz"),
            _ => PickNearest.Replace("%(WANT)s", "A3.xyz"),
        };
        return Lf(PickFunctionTemplate)
            .Replace("%(N)d", ringEntries.ToString())
            .Replace("%(CAP)s", Hf(cap))
            .Replace("%(AHEAD)s", Hf(ahead))
            .Replace("%(SAME)s", Hf(same))
            .Replace("%(DUP)s", Hf(dup))
            .Replace("%(RINGTEX)s", ringTex)
            .Replace("%(S)d", RingSlotRows(packet).ToString())
            .Replace("%(H)d", PacketHeight(packet).ToString())
            .Replace("%(CHOOSE)s", Lf(choose));
    }

    /// <summary>Stamp the pick pass of one source mesh a bone's row places: it carries
    /// <paramref name="rest"/> (the root's rest origin in bind space) by palette slot <paramref name="slot"/>'s
    /// rows and then the replaced part's rows, and reads the row mask at that slot: a row this draw did not
    /// recover itself places no copy, and the part is absent at this draw. The ring's slots span
    /// <see cref="RingSlotRows"/> rows for the mesh's packet of <paramref name="packet"/> entries.</summary>
    public static string EmitPosePick(int ringEntries, float cap, float ahead, float same, float dup, uint slot,
        (float X, float Y, float Z) rest, int packet) =>
        Lf(PosePickTemplate)
            .Replace("%(MASKDECL)s",
                "Texture2D<float4> mask : register(t2);   // per palette pixel: 1 where this draw recovered the slot's bone itself\n")
            .Replace("%(PICKFN)s", PickFunction(PickRule.ByRow, ringEntries, cap, ahead, same, dup, "ring", packet, slot, rest))
            .Replace("%(MASK)s",
                "    // the row placing this part is this draw's own recovery of its bone, or the part is absent here\n"
                + $"    if(mask.Load(int3({4 * slot},0,0)).x<0.5) pick=-1;\n");

    /// <summary>A float as an HLSL literal that reads back to the same value.</summary>
    static string Hf(float v)
    {
        string s = v.ToString("R", System.Globalization.CultureInfo.InvariantCulture).Replace("E", "e");
        return s.Contains('.') || s.Contains('e') ? s : s + ".0";
    }
}
