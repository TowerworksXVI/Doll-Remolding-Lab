using System.Collections.Generic;
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
RWBuffer<float4>      palOut : register(u1);    // 4*unionBones rows (shared across all pool parts)
static const uint ROWS=%(ROWS)d;
[numthreads(64,1,1)]
void main(uint3 tid : SV_DispatchThreadID){
    uint i=tid.x; if(i>=ROWS) return;
    uint localBone=i>>2, comp=i&3;
    uint u=Map[localBone];
    if(u==0xFFFFFFFF) return;   // this part does NOT own the bone (another part supports it better) -> don't clobber
    uint sbase=Off[localBone<<1], width=Off[(localBone<<1)|1];
    float3 a=float3(0,0,0); uint cbase=(sbase<<2)+comp*width;
    for(uint t=0;t<width;t++) a+=Cpinv[cbase+t]*q[Sel[sbase+t]].position;
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
RWBuffer<float4>      palOut : register(u1);    // 4*unionBones rows (shared across all pool parts)
static const uint N=%(N)d, ROWS=%(ROWS)d;
[numthreads(64,1,1)]
void main(uint3 tid : SV_DispatchThreadID){
    uint i=tid.x; if(i>=ROWS) return;
    uint localBone=i>>2, comp=i&3;
    uint u=Map[localBone];
    if(u==0xFFFFFFFF) return;   // this part does NOT own the bone (another part supports it better) -> don't clobber
    float3 a=float3(0,0,0); uint base=i*N;
    for(uint v=0;v<N;v++) a+=Cpinv[base+v]*q[v].position;
    palOut[(u<<2)|comp]=float4(a,(comp==3)?1.0:0.0);   // scatter into this bone's union slot
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
RWBuffer<float4>         palOut    : register(u1);
static const uint ROWS=%(ROWS)d;

float4x4 AffineInverse(float4 r0, float4 r1, float4 r2, float4 r3){
    float3 a=r0.xyz, b=r1.xyz, c=r2.xyz, t=r3.xyz;
    float3 bc=cross(b,c), ca=cross(c,a), ab=cross(a,b);
    float det=dot(a,bc);
    float3 i0=float3(bc.x,ca.x,ab.x)/det;    // rows of R^-1 (row-vector convention)
    float3 i1=float3(bc.y,ca.y,ab.y)/det;
    float3 i2=float3(bc.z,ca.z,ab.z)/det;
    float3 ti=-(t.x*i0+t.y*i1+t.z*i2);
    return float4x4(float4(i0,0),float4(i1,0),float4(i2,0),float4(ti,1));
}

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
RWBuffer<float4>         palOut    : register(u1);
static const uint ROWS=%(ROWS)d;      // 4*unionBones — witness slots beyond are inputs only
static const uint ANCHOR=%(ANCHOR)d;
static const uint2 WIT[%(P)d] = { %(WIT)s };   // per part: base row of (partSide, anchorSide) witness; x=0xFFFFFFFF = no witness (keep the row as-is)

float4x4 AffineInverse(float4 r0, float4 r1, float4 r2, float4 r3){
    float3 a=r0.xyz, b=r1.xyz, c=r2.xyz, t=r3.xyz;
    float3 bc=cross(b,c), ca=cross(c,a), ab=cross(a,b);
    float det=dot(a,bc);
    float3 i0=float3(bc.x,ca.x,ab.x)/det;    // rows of R^-1 (row-vector convention)
    float3 i1=float3(bc.y,ca.y,ab.y)/det;
    float3 i2=float3(bc.z,ca.z,ab.z)/det;
    float3 ti=-(t.x*i0+t.y*i1+t.z*i2);
    return float4x4(float4(i0,0),float4(i1,0),float4(i2,0),float4(ti,1));
}

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

    public const string SkinTemplate =
@"// Skin the new body (VCOUNT verts, weighted to UNION bone order) with the CONVERTED palette.
struct Vtx  { float3 position; float3 normal; float4 tangent; };
struct Skin { float4 weight;   uint4  index;  };
struct Mat  { float4 r0, r1, r2, r3; };
// u1 is the draw's own vertex buffer bound directly: a buffer carrying the vertex-buffer bind flag
// cannot be structured, so the 40-byte vertex is stored raw, by byte offset.
RWByteAddressBuffer     rw_out : register(u1);
StructuredBuffer<Vtx>   bindV  : register(t0);
StructuredBuffer<Skin>  skinB  : register(t1);
StructuredBuffer<Mat>   palB   : register(t2);
static const uint VCOUNT=%(VCOUNT)d;
[numthreads(64,1,1)]
void main(uint3 tid : SV_DispatchThreadID){
    uint vid=tid.x; if(vid>=VCOUNT) return;
    Vtx V=bindV[vid]; Skin S=skinB[vid];
    float3 sp=0,sn=0,st=0;
    [unroll] for(int k=0;k<4;k++){
        float wk=S.weight[k]; uint b=S.index[k];
        Mat Mb=palB[b]; float4x4 M=float4x4(Mb.r0,Mb.r1,Mb.r2,Mb.r3);
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

    static string Lf(string s) => s.Replace("\r\n", "\n");

    /// <summary>Stamp the SLIM recover shader: dispatch rows ROWS. Per-bone anchor widths are data (the
    /// Off buffer), not compile-time constants.</summary>
    public static string EmitRecover(int rows) =>
        Lf(RecoverTemplate).Replace("%(ROWS)d", rows.ToString());

    /// <summary>Stamp the DENSE recover shader (the whole part ships dense): vertex count N, rows ROWS.</summary>
    public static string EmitRecoverDense(int n, int rows) =>
        Lf(RecoverDenseTemplate).Replace("%(N)d", n.ToString()).Replace("%(ROWS)d", rows.ToString());

    /// <summary>Stamp the convert shader: per-part cbuffer block, owner-indexed selection block, union
    /// row count (4*unionBones).</summary>
    public static string EmitConvert(int partCount, int unionBones)
    {
        var cbufs = new StringBuilder();
        for (int pi = 0; pi < partCount; pi++)
        {
            if (pi > 0) cbufs.Append('\n');
            cbufs.Append($"cbuffer PartCB{pi} : register(b{5 + pi}) {{ float4 W{pi}[4]; }}");
        }
        var sel = new StringBuilder();
        for (int pi = 0; pi < partCount; pi++)
        {
            if (pi > 0) sel.Append('\n');
            sel.Append($"    {(pi == 0 ? "if" : "else if")}(pi=={pi}) WP=float4x4(W{pi}[0],W{pi}[1],W{pi}[2],W{pi}[3]);");
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

    /// <summary>Stamp the skin shader with the body vertex count.</summary>
    public static string EmitSkin(int vcount) =>
        Lf(SkinTemplate).Replace("%(VCOUNT)d", vcount.ToString());
}
