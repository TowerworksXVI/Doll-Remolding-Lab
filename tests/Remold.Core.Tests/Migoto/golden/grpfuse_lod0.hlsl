// One wardrobe-group member's own draw, fused: recover this group's bones from the MEMBER's posed
// vertices and rebase the rows into the anchor's draw space in the same dispatch. Exactly one variant
// of the slot is worn and an unworn variant issues no draws, so whichever member drew last wrote these
// rows — the draw stream is the freshness rule, and there is no per-frame flag.
// Rows land in the group's APPENDED slot region of the CONVERTED palette, past the union and the
// witness slots; the convert passes write only union rows, so the copy round-trip carries these
// through untouched.
// K comes from CONSTANTS: W_member is this draw's own vs-cb1, W_anchor the anchor's captured one.
// ROWS = 4*groupBones; BASE = the group's first appended slot.
struct Vtx { float3 position; float3 normal; float4 tangent; };
StructuredBuffer<Vtx> q      : register(t0);   // the member's posed vb0, captured at this draw
Buffer<float>         Cpinv  : register(t1);
Buffer<uint>          Map    : register(t2);   // per GROUP bone: this member's local bone, or 0xFFFFFFFF
RWBuffer<float4>      palOut : register(u1);
cbuffer MemberCB : register(b5)  { float4 WM[4]; }
cbuffer AnchorCB : register(b13) { float4 WA[4]; }
static const uint ROWS=4, BASE=4;
Buffer<uint>          Sel    : register(t3);   // anchor vertex indices, bone b at [base, base+width)
Buffer<uint>          Off    : register(t4);   // 2 per bone: base, width
float4 Row(uint b, uint comp){
    uint sbase=Off[b<<1], width=Off[(b<<1)|1];
    float3 a=float3(0,0,0); uint cbase=(sbase<<2)+comp*width;
    for(uint t=0;t<width;t++) a+=Cpinv[cbase+t]*q[Sel[sbase+t]].position;
    return float4(a,(comp==3)?1.0:0.0);
}

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
    uint g=i>>2, comp=i&3;
    uint b=Map[g];
    if(b==0xFFFFFFFF) return;   // this member cannot condition the bone -> leave the row alone
    float4x4 K=mul(float4x4(WM[0],WM[1],WM[2],WM[3]), AffineInverse(WA[0],WA[1],WA[2],WA[3]));
    palOut[((BASE+g)<<2)|comp]=mul(Row(b,comp), K);
}
