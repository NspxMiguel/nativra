// Tests for the d3dx9_43 maths. D3DX was removed from Windows, so there is no
// reference implementation to diff against; instead these check mathematical
// identities (M * M^-1 = I, transpose is an involution, det of a rotation is 1)
// and a set of hand-computed reference results (a 90-degree rotation, a
// perspective projection mapping near/far to 0/1, quaternion round-trips).
//
// The maths source is linked directly so the checks are unit tests; the
// workflow separately builds d3dx9_43.dll to confirm the exports.

#define D3DX9_EXPORTS
#include "../d3dx9_43/d3dx9math.h"
#include <cstdio>
#include <cmath>

static int g_failures = 0;
#define CHECK(cond, msg) do { if (!(cond)) { printf("FAIL: %s\n", msg); ++g_failures; } } while (0)

static bool Near(float a, float b, float eps = 1e-4f) { return fabsf(a - b) <= eps; }

static bool MatNear(const D3DXMATRIX& a, const D3DXMATRIX& b, float eps = 1e-4f)
{
    for (int i = 0; i < 4; ++i)
        for (int j = 0; j < 4; ++j)
            if (!Near(a.m[i][j], b.m[i][j], eps)) return false;
    return true;
}

static D3DXMATRIX Ident()
{
    D3DXMATRIX m;
    for (int i = 0; i < 4; ++i) for (int j = 0; j < 4; ++j) m.m[i][j] = (i == j) ? 1.0f : 0.0f;
    return m;
}

int main()
{
    const float PI = 3.14159265358979323846f;

    // Identity is the multiplicative unit.
    {
        D3DXMATRIX a, r;
        D3DXMatrixRotationYawPitchRoll(&a, 0.3f, -0.7f, 1.1f);
        D3DXMATRIX id = Ident();
        D3DXMatrixMultiply(&r, &a, &id);
        CHECK(MatNear(r, a), "M * I = M");
        D3DXMatrixMultiply(&r, &id, &a);
        CHECK(MatNear(r, a), "I * M = M");
    }

    // Transpose is an involution; determinant of a pure rotation is 1.
    {
        D3DXMATRIX a, t, tt;
        D3DXMatrixRotationYawPitchRoll(&a, 0.9f, 0.2f, -0.4f);
        D3DXMatrixTranspose(&t, &a);
        D3DXMatrixTranspose(&tt, &t);
        CHECK(MatNear(tt, a), "transpose twice = original");
        CHECK(Near(D3DXMatrixDeterminant(&a), 1.0f), "det(rotation) = 1");

        D3DXMATRIX s;
        D3DXMatrixScaling(&s, 2.0f, 3.0f, 4.0f);
        CHECK(Near(D3DXMatrixDeterminant(&s), 24.0f), "det(scale) = product");
    }

    // Inverse: M * M^-1 = I for a rotation-translation.
    {
        D3DXMATRIX rot, trans, m, inv, prod;
        D3DXMatrixRotationYawPitchRoll(&rot, 0.5f, 1.2f, -0.3f);
        D3DXMatrixTranslation(&trans, 3.0f, -2.0f, 5.0f);
        D3DXMatrixMultiply(&m, &rot, &trans);
        float det = 0;
        CHECK(D3DXMatrixInverse(&inv, &det, &m) != nullptr, "inverse exists");
        D3DXMatrixMultiply(&prod, &m, &inv);
        CHECK(MatNear(prod, Ident(), 1e-3f), "M * inv(M) = I");
    }

    // A 90-degree rotation about Y sends +Z to +X (left-handed).
    {
        D3DXMATRIX ry;
        D3DXMatrixRotationY(&ry, PI * 0.5f);
        D3DXVECTOR3 v = { 0, 0, 1 }, out;
        D3DXVec3TransformNormal(&out, &v, &ry);
        CHECK(Near(out.x, 1.0f) && Near(out.y, 0.0f) && Near(out.z, 0.0f), "RotationY(90) : +Z -> +X");
    }

    // Perspective maps the near and far planes to NDC z = 0 and z = 1.
    {
        D3DXMATRIX p;
        float zn = 1.0f, zf = 100.0f;
        D3DXMatrixPerspectiveFovLH(&p, PI * 0.25f, 16.0f / 9.0f, zn, zf);
        D3DXVECTOR3 nearP = { 0, 0, zn }, farP = { 0, 0, zf };
        D3DXVECTOR4 rn, rf;
        D3DXVec3Transform(&rn, &nearP, &p);
        D3DXVec3Transform(&rf, &farP, &p);
        CHECK(Near(rn.z / rn.w, 0.0f), "near plane -> NDC 0");
        CHECK(Near(rf.z / rf.w, 1.0f), "far plane -> NDC 1");
    }

    // LookAtLH at the origin looking down +Z with +Y up leaves a forward point ahead.
    {
        D3DXMATRIX v;
        D3DXVECTOR3 eye = { 0, 0, 0 }, at = { 0, 0, 1 }, up = { 0, 1, 0 };
        D3DXMatrixLookAtLH(&v, &eye, &at, &up);
        D3DXVECTOR3 world = { 0, 0, 5 }, view;
        D3DXVec3TransformCoord(&view, &world, &v);
        CHECK(Near(view.x, 0.0f) && Near(view.y, 0.0f) && Near(view.z, 5.0f), "LookAtLH identity view");
    }

    // Quaternion round-trip: matrix -> quaternion -> matrix.
    {
        D3DXMATRIX rm, back;
        D3DXMatrixRotationYawPitchRoll(&rm, 0.6f, -0.8f, 0.25f);
        D3DXQUATERNION q;
        D3DXQuaternionRotationMatrix(&q, &rm);
        D3DXMatrixRotationQuaternion(&back, &q);
        CHECK(MatNear(back, rm, 1e-3f), "matrix -> quat -> matrix round-trip");

        // YawPitchRoll quaternion matches the YawPitchRoll matrix.
        D3DXQUATERNION qy;
        D3DXQuaternionRotationYawPitchRoll(&qy, 0.6f, -0.8f, 0.25f);
        D3DXMATRIX fromQ;
        D3DXMatrixRotationQuaternion(&fromQ, &qy);
        CHECK(MatNear(fromQ, rm, 1e-3f), "quat YPR matches matrix YPR");
    }

    // Slerp endpoints and unit-length interpolation.
    {
        D3DXQUATERNION a, b, r;
        D3DXVECTOR3 ax = { 0, 1, 0 };
        D3DXQuaternionRotationAxis(&a, &ax, 0.0f);
        D3DXQuaternionRotationAxis(&b, &ax, PI * 0.5f);
        D3DXQuaternionSlerp(&r, &a, &b, 0.0f);
        CHECK(Near(r.w, a.w) && Near(r.y, a.y), "slerp t=0 -> a");
        D3DXQuaternionSlerp(&r, &a, &b, 1.0f);
        CHECK(Near(r.w, b.w, 1e-3f) && Near(r.y, b.y, 1e-3f), "slerp t=1 -> b");
        D3DXQuaternionSlerp(&r, &a, &b, 0.5f);
        CHECK(Near(sqrtf(r.x*r.x + r.y*r.y + r.z*r.z + r.w*r.w), 1.0f, 1e-3f), "slerp stays unit length");
    }

    // Vec3Normalize gives a unit vector along the input.
    {
        D3DXVECTOR3 v = { 3, 0, 4 }, n;
        D3DXVec3Normalize(&n, &v);
        CHECK(Near(n.x, 0.6f) && Near(n.z, 0.8f), "Vec3Normalize");
    }

    if (g_failures == 0) printf("d3dx9_43: all checks passed\n");
    else printf("d3dx9_43: %d check(s) failed\n", g_failures);
    return g_failures == 0 ? 0 : 1;
}
