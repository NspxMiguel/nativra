// d3dx9_43.dll maths — a real implementation of the D3DX9 helpers, since D3DX
// was removed from Windows and there is nothing to forward to. Row-major
// matrices with the row-vector convention (v' = v * M), matching the D3DX ABI.

#define D3DX9_EXPORTS
#include "d3dx9math.h"
#include <cmath>

namespace
{
    inline float Dot3(const D3DXVECTOR3& a, const D3DXVECTOR3& b) { return a.x*b.x + a.y*b.y + a.z*b.z; }
    inline D3DXVECTOR3 Cross3(const D3DXVECTOR3& a, const D3DXVECTOR3& b)
    {
        return { a.y*b.z - a.z*b.y, a.z*b.x - a.x*b.z, a.x*b.y - a.y*b.x };
    }
}

// ----------------------------------------------------------------- matrices

D3DXMATRIX* WINAPI D3DXMatrixMultiply(D3DXMATRIX* out, const D3DXMATRIX* a, const D3DXMATRIX* b)
{
    D3DXMATRIX r;
    for (int i = 0; i < 4; ++i)
        for (int j = 0; j < 4; ++j)
            r.m[i][j] = a->m[i][0]*b->m[0][j] + a->m[i][1]*b->m[1][j] +
                        a->m[i][2]*b->m[2][j] + a->m[i][3]*b->m[3][j];
    *out = r;
    return out;
}

D3DXMATRIX* WINAPI D3DXMatrixTranspose(D3DXMATRIX* out, const D3DXMATRIX* in)
{
    D3DXMATRIX r;
    for (int i = 0; i < 4; ++i)
        for (int j = 0; j < 4; ++j)
            r.m[i][j] = in->m[j][i];
    *out = r;
    return out;
}

D3DXMATRIX* WINAPI D3DXMatrixMultiplyTranspose(D3DXMATRIX* out, const D3DXMATRIX* a, const D3DXMATRIX* b)
{
    D3DXMATRIX t;
    D3DXMatrixMultiply(&t, a, b);
    return D3DXMatrixTranspose(out, &t);
}

float WINAPI D3DXMatrixDeterminant(const D3DXMATRIX* m)
{
    // Cofactor expansion using the 2x2 minors of the last two rows.
    float s0 = m->m[0][0]*m->m[1][1] - m->m[1][0]*m->m[0][1];
    float s1 = m->m[0][0]*m->m[1][2] - m->m[1][0]*m->m[0][2];
    float s2 = m->m[0][0]*m->m[1][3] - m->m[1][0]*m->m[0][3];
    float s3 = m->m[0][1]*m->m[1][2] - m->m[1][1]*m->m[0][2];
    float s4 = m->m[0][1]*m->m[1][3] - m->m[1][1]*m->m[0][3];
    float s5 = m->m[0][2]*m->m[1][3] - m->m[1][2]*m->m[0][3];
    float c5 = m->m[2][2]*m->m[3][3] - m->m[3][2]*m->m[2][3];
    float c4 = m->m[2][1]*m->m[3][3] - m->m[3][1]*m->m[2][3];
    float c3 = m->m[2][1]*m->m[3][2] - m->m[3][1]*m->m[2][2];
    float c2 = m->m[2][0]*m->m[3][3] - m->m[3][0]*m->m[2][3];
    float c1 = m->m[2][0]*m->m[3][2] - m->m[3][0]*m->m[2][2];
    float c0 = m->m[2][0]*m->m[3][1] - m->m[3][0]*m->m[2][1];
    return s0*c5 - s1*c4 + s2*c3 + s3*c2 - s4*c1 + s5*c0;
}

D3DXMATRIX* WINAPI D3DXMatrixInverse(D3DXMATRIX* out, float* determinant, const D3DXMATRIX* m)
{
    float s0 = m->m[0][0]*m->m[1][1] - m->m[1][0]*m->m[0][1];
    float s1 = m->m[0][0]*m->m[1][2] - m->m[1][0]*m->m[0][2];
    float s2 = m->m[0][0]*m->m[1][3] - m->m[1][0]*m->m[0][3];
    float s3 = m->m[0][1]*m->m[1][2] - m->m[1][1]*m->m[0][2];
    float s4 = m->m[0][1]*m->m[1][3] - m->m[1][1]*m->m[0][3];
    float s5 = m->m[0][2]*m->m[1][3] - m->m[1][2]*m->m[0][3];
    float c5 = m->m[2][2]*m->m[3][3] - m->m[3][2]*m->m[2][3];
    float c4 = m->m[2][1]*m->m[3][3] - m->m[3][1]*m->m[2][3];
    float c3 = m->m[2][1]*m->m[3][2] - m->m[3][1]*m->m[2][2];
    float c2 = m->m[2][0]*m->m[3][3] - m->m[3][0]*m->m[2][3];
    float c1 = m->m[2][0]*m->m[3][2] - m->m[3][0]*m->m[2][2];
    float c0 = m->m[2][0]*m->m[3][1] - m->m[3][0]*m->m[2][1];

    float det = s0*c5 - s1*c4 + s2*c3 + s3*c2 - s4*c1 + s5*c0;
    if (determinant) *determinant = det;
    if (det == 0.0f) return nullptr;
    float invDet = 1.0f / det;

    D3DXMATRIX r;
    r.m[0][0] = ( m->m[1][1]*c5 - m->m[1][2]*c4 + m->m[1][3]*c3) * invDet;
    r.m[0][1] = (-m->m[0][1]*c5 + m->m[0][2]*c4 - m->m[0][3]*c3) * invDet;
    r.m[0][2] = ( m->m[3][1]*s5 - m->m[3][2]*s4 + m->m[3][3]*s3) * invDet;
    r.m[0][3] = (-m->m[2][1]*s5 + m->m[2][2]*s4 - m->m[2][3]*s3) * invDet;
    r.m[1][0] = (-m->m[1][0]*c5 + m->m[1][2]*c2 - m->m[1][3]*c1) * invDet;
    r.m[1][1] = ( m->m[0][0]*c5 - m->m[0][2]*c2 + m->m[0][3]*c1) * invDet;
    r.m[1][2] = (-m->m[3][0]*s5 + m->m[3][2]*s2 - m->m[3][3]*s1) * invDet;
    r.m[1][3] = ( m->m[2][0]*s5 - m->m[2][2]*s2 + m->m[2][3]*s1) * invDet;
    r.m[2][0] = ( m->m[1][0]*c4 - m->m[1][1]*c2 + m->m[1][3]*c0) * invDet;
    r.m[2][1] = (-m->m[0][0]*c4 + m->m[0][1]*c2 - m->m[0][3]*c0) * invDet;
    r.m[2][2] = ( m->m[3][0]*s4 - m->m[3][1]*s2 + m->m[3][3]*s0) * invDet;
    r.m[2][3] = (-m->m[2][0]*s4 + m->m[2][1]*s2 - m->m[2][3]*s0) * invDet;
    r.m[3][0] = (-m->m[1][0]*c3 + m->m[1][1]*c1 - m->m[1][2]*c0) * invDet;
    r.m[3][1] = ( m->m[0][0]*c3 - m->m[0][1]*c1 + m->m[0][2]*c0) * invDet;
    r.m[3][2] = (-m->m[3][0]*s3 + m->m[3][1]*s1 - m->m[3][2]*s0) * invDet;
    r.m[3][3] = ( m->m[2][0]*s3 - m->m[2][1]*s1 + m->m[2][2]*s0) * invDet;
    *out = r;
    return out;
}

static void Identity(D3DXMATRIX* out)
{
    for (int i = 0; i < 4; ++i)
        for (int j = 0; j < 4; ++j)
            out->m[i][j] = (i == j) ? 1.0f : 0.0f;
}

D3DXMATRIX* WINAPI D3DXMatrixTranslation(D3DXMATRIX* out, float x, float y, float z)
{
    Identity(out);
    out->_41 = x; out->_42 = y; out->_43 = z;
    return out;
}

D3DXMATRIX* WINAPI D3DXMatrixScaling(D3DXMATRIX* out, float x, float y, float z)
{
    Identity(out);
    out->_11 = x; out->_22 = y; out->_33 = z;
    return out;
}

D3DXMATRIX* WINAPI D3DXMatrixRotationX(D3DXMATRIX* out, float a)
{
    Identity(out);
    float c = cosf(a), s = sinf(a);
    out->_22 = c; out->_23 = s; out->_32 = -s; out->_33 = c;
    return out;
}

D3DXMATRIX* WINAPI D3DXMatrixRotationY(D3DXMATRIX* out, float a)
{
    Identity(out);
    float c = cosf(a), s = sinf(a);
    out->_11 = c; out->_13 = -s; out->_31 = s; out->_33 = c;
    return out;
}

D3DXMATRIX* WINAPI D3DXMatrixRotationZ(D3DXMATRIX* out, float a)
{
    Identity(out);
    float c = cosf(a), s = sinf(a);
    out->_11 = c; out->_12 = s; out->_21 = -s; out->_22 = c;
    return out;
}

D3DXMATRIX* WINAPI D3DXMatrixRotationAxis(D3DXMATRIX* out, const D3DXVECTOR3* axis, float angle)
{
    float len = sqrtf(Dot3(*axis, *axis));
    D3DXVECTOR3 n = len > 0 ? D3DXVECTOR3{ axis->x/len, axis->y/len, axis->z/len } : D3DXVECTOR3{ 0, 0, 0 };
    float c = cosf(angle), s = sinf(angle), t = 1.0f - c;
    Identity(out);
    out->_11 = t*n.x*n.x + c;      out->_12 = t*n.x*n.y + s*n.z;  out->_13 = t*n.x*n.z - s*n.y;
    out->_21 = t*n.x*n.y - s*n.z;  out->_22 = t*n.y*n.y + c;      out->_23 = t*n.y*n.z + s*n.x;
    out->_31 = t*n.x*n.z + s*n.y;  out->_32 = t*n.y*n.z - s*n.x;  out->_33 = t*n.z*n.z + c;
    return out;
}

D3DXMATRIX* WINAPI D3DXMatrixRotationQuaternion(D3DXMATRIX* out, const D3DXQUATERNION* q)
{
    float x = q->x, y = q->y, z = q->z, w = q->w;
    Identity(out);
    out->_11 = 1 - 2*(y*y + z*z); out->_12 = 2*(x*y + z*w);     out->_13 = 2*(x*z - y*w);
    out->_21 = 2*(x*y - z*w);     out->_22 = 1 - 2*(x*x + z*z); out->_23 = 2*(y*z + x*w);
    out->_31 = 2*(x*z + y*w);     out->_32 = 2*(y*z - x*w);     out->_33 = 1 - 2*(x*x + y*y);
    return out;
}

D3DXMATRIX* WINAPI D3DXMatrixRotationYawPitchRoll(D3DXMATRIX* out, float yaw, float pitch, float roll)
{
    // D3DX applies roll (Z), then pitch (X), then yaw (Y): M = Rz * Rx * Ry.
    D3DXMATRIX rz, rx, ry, t;
    D3DXMatrixRotationZ(&rz, roll);
    D3DXMatrixRotationX(&rx, pitch);
    D3DXMatrixRotationY(&ry, yaw);
    D3DXMatrixMultiply(&t, &rz, &rx);
    D3DXMatrixMultiply(out, &t, &ry);
    return out;
}

D3DXMATRIX* WINAPI D3DXMatrixLookAtLH(D3DXMATRIX* out, const D3DXVECTOR3* eye, const D3DXVECTOR3* at, const D3DXVECTOR3* up)
{
    D3DXVECTOR3 zaxis = { at->x - eye->x, at->y - eye->y, at->z - eye->z };
    float zl = sqrtf(Dot3(zaxis, zaxis)); zaxis = { zaxis.x/zl, zaxis.y/zl, zaxis.z/zl };
    D3DXVECTOR3 xaxis = Cross3(*up, zaxis);
    float xl = sqrtf(Dot3(xaxis, xaxis)); xaxis = { xaxis.x/xl, xaxis.y/xl, xaxis.z/xl };
    D3DXVECTOR3 yaxis = Cross3(zaxis, xaxis);
    Identity(out);
    out->_11 = xaxis.x; out->_12 = yaxis.x; out->_13 = zaxis.x;
    out->_21 = xaxis.y; out->_22 = yaxis.y; out->_23 = zaxis.y;
    out->_31 = xaxis.z; out->_32 = yaxis.z; out->_33 = zaxis.z;
    out->_41 = -Dot3(xaxis, *eye); out->_42 = -Dot3(yaxis, *eye); out->_43 = -Dot3(zaxis, *eye);
    return out;
}

D3DXMATRIX* WINAPI D3DXMatrixLookAtRH(D3DXMATRIX* out, const D3DXVECTOR3* eye, const D3DXVECTOR3* at, const D3DXVECTOR3* up)
{
    D3DXVECTOR3 zaxis = { eye->x - at->x, eye->y - at->y, eye->z - at->z };
    float zl = sqrtf(Dot3(zaxis, zaxis)); zaxis = { zaxis.x/zl, zaxis.y/zl, zaxis.z/zl };
    D3DXVECTOR3 xaxis = Cross3(*up, zaxis);
    float xl = sqrtf(Dot3(xaxis, xaxis)); xaxis = { xaxis.x/xl, xaxis.y/xl, xaxis.z/xl };
    D3DXVECTOR3 yaxis = Cross3(zaxis, xaxis);
    Identity(out);
    out->_11 = xaxis.x; out->_12 = yaxis.x; out->_13 = zaxis.x;
    out->_21 = xaxis.y; out->_22 = yaxis.y; out->_23 = zaxis.y;
    out->_31 = xaxis.z; out->_32 = yaxis.z; out->_33 = zaxis.z;
    out->_41 = -Dot3(xaxis, *eye); out->_42 = -Dot3(yaxis, *eye); out->_43 = -Dot3(zaxis, *eye);
    return out;
}

D3DXMATRIX* WINAPI D3DXMatrixPerspectiveFovLH(D3DXMATRIX* out, float fovY, float aspect, float zn, float zf)
{
    float h = 1.0f / tanf(fovY * 0.5f);
    float w = h / aspect;
    for (int i = 0; i < 16; ++i) ((float*)out->m)[i] = 0.0f;
    out->_11 = w; out->_22 = h;
    out->_33 = zf / (zf - zn); out->_34 = 1.0f;
    out->_43 = -zn * zf / (zf - zn);
    return out;
}

D3DXMATRIX* WINAPI D3DXMatrixPerspectiveFovRH(D3DXMATRIX* out, float fovY, float aspect, float zn, float zf)
{
    float h = 1.0f / tanf(fovY * 0.5f);
    float w = h / aspect;
    for (int i = 0; i < 16; ++i) ((float*)out->m)[i] = 0.0f;
    out->_11 = w; out->_22 = h;
    out->_33 = zf / (zn - zf); out->_34 = -1.0f;
    out->_43 = zn * zf / (zn - zf);
    return out;
}

D3DXMATRIX* WINAPI D3DXMatrixOrthoLH(D3DXMATRIX* out, float w, float h, float zn, float zf)
{
    Identity(out);
    out->_11 = 2.0f / w; out->_22 = 2.0f / h;
    out->_33 = 1.0f / (zf - zn); out->_43 = zn / (zn - zf);
    return out;
}

D3DXMATRIX* WINAPI D3DXMatrixOrthoRH(D3DXMATRIX* out, float w, float h, float zn, float zf)
{
    Identity(out);
    out->_11 = 2.0f / w; out->_22 = 2.0f / h;
    out->_33 = 1.0f / (zn - zf); out->_43 = zn / (zn - zf);
    return out;
}

D3DXMATRIX* WINAPI D3DXMatrixOrthoOffCenterLH(D3DXMATRIX* out, float l, float r, float b, float t, float zn, float zf)
{
    Identity(out);
    out->_11 = 2.0f / (r - l); out->_22 = 2.0f / (t - b); out->_33 = 1.0f / (zf - zn);
    out->_41 = (l + r) / (l - r); out->_42 = (t + b) / (b - t); out->_43 = zn / (zn - zf);
    return out;
}

// ------------------------------------------------------------------ vectors

D3DXVECTOR3* WINAPI D3DXVec3Normalize(D3DXVECTOR3* out, const D3DXVECTOR3* in)
{
    float len = sqrtf(in->x*in->x + in->y*in->y + in->z*in->z);
    if (len > 0) { out->x = in->x/len; out->y = in->y/len; out->z = in->z/len; }
    else { *out = { 0, 0, 0 }; }
    return out;
}

D3DXVECTOR4* WINAPI D3DXVec3Transform(D3DXVECTOR4* out, const D3DXVECTOR3* v, const D3DXMATRIX* m)
{
    D3DXVECTOR4 r;
    r.x = v->x*m->_11 + v->y*m->_21 + v->z*m->_31 + m->_41;
    r.y = v->x*m->_12 + v->y*m->_22 + v->z*m->_32 + m->_42;
    r.z = v->x*m->_13 + v->y*m->_23 + v->z*m->_33 + m->_43;
    r.w = v->x*m->_14 + v->y*m->_24 + v->z*m->_34 + m->_44;
    *out = r;
    return out;
}

D3DXVECTOR3* WINAPI D3DXVec3TransformCoord(D3DXVECTOR3* out, const D3DXVECTOR3* v, const D3DXMATRIX* m)
{
    D3DXVECTOR4 r;
    D3DXVec3Transform(&r, v, m);
    float iw = r.w != 0 ? 1.0f / r.w : 1.0f;
    *out = { r.x*iw, r.y*iw, r.z*iw };
    return out;
}

D3DXVECTOR3* WINAPI D3DXVec3TransformNormal(D3DXVECTOR3* out, const D3DXVECTOR3* v, const D3DXMATRIX* m)
{
    D3DXVECTOR3 r;
    r.x = v->x*m->_11 + v->y*m->_21 + v->z*m->_31;
    r.y = v->x*m->_12 + v->y*m->_22 + v->z*m->_32;
    r.z = v->x*m->_13 + v->y*m->_23 + v->z*m->_33;
    *out = r;
    return out;
}

D3DXVECTOR4* WINAPI D3DXVec4Transform(D3DXVECTOR4* out, const D3DXVECTOR4* v, const D3DXMATRIX* m)
{
    D3DXVECTOR4 r;
    r.x = v->x*m->_11 + v->y*m->_21 + v->z*m->_31 + v->w*m->_41;
    r.y = v->x*m->_12 + v->y*m->_22 + v->z*m->_32 + v->w*m->_42;
    r.z = v->x*m->_13 + v->y*m->_23 + v->z*m->_33 + v->w*m->_43;
    r.w = v->x*m->_14 + v->y*m->_24 + v->z*m->_34 + v->w*m->_44;
    *out = r;
    return out;
}

D3DXVECTOR4* WINAPI D3DXVec4Normalize(D3DXVECTOR4* out, const D3DXVECTOR4* in)
{
    float len = sqrtf(in->x*in->x + in->y*in->y + in->z*in->z + in->w*in->w);
    if (len > 0) { *out = { in->x/len, in->y/len, in->z/len, in->w/len }; }
    else { *out = { 0, 0, 0, 0 }; }
    return out;
}

D3DXVECTOR2* WINAPI D3DXVec2Normalize(D3DXVECTOR2* out, const D3DXVECTOR2* in)
{
    float len = sqrtf(in->x*in->x + in->y*in->y);
    if (len > 0) { *out = { in->x/len, in->y/len }; }
    else { *out = { 0, 0 }; }
    return out;
}

// --------------------------------------------------------------- quaternions

D3DXQUATERNION* WINAPI D3DXQuaternionRotationMatrix(D3DXQUATERNION* out, const D3DXMATRIX* m)
{
    float trace = m->_11 + m->_22 + m->_33;
    if (trace > 0.0f)
    {
        float s = sqrtf(trace + 1.0f) * 2.0f;
        out->w = 0.25f * s;
        out->x = (m->_23 - m->_32) / s;
        out->y = (m->_31 - m->_13) / s;
        out->z = (m->_12 - m->_21) / s;
    }
    else if (m->_11 > m->_22 && m->_11 > m->_33)
    {
        float s = sqrtf(1.0f + m->_11 - m->_22 - m->_33) * 2.0f;
        out->w = (m->_23 - m->_32) / s;
        out->x = 0.25f * s;
        out->y = (m->_12 + m->_21) / s;
        out->z = (m->_31 + m->_13) / s;
    }
    else if (m->_22 > m->_33)
    {
        float s = sqrtf(1.0f + m->_22 - m->_11 - m->_33) * 2.0f;
        out->w = (m->_31 - m->_13) / s;
        out->x = (m->_12 + m->_21) / s;
        out->y = 0.25f * s;
        out->z = (m->_23 + m->_32) / s;
    }
    else
    {
        float s = sqrtf(1.0f + m->_33 - m->_11 - m->_22) * 2.0f;
        out->w = (m->_12 - m->_21) / s;
        out->x = (m->_31 + m->_13) / s;
        out->y = (m->_23 + m->_32) / s;
        out->z = 0.25f * s;
    }
    return out;
}

D3DXQUATERNION* WINAPI D3DXQuaternionRotationYawPitchRoll(D3DXQUATERNION* out, float yaw, float pitch, float roll)
{
    float hy = yaw * 0.5f, hp = pitch * 0.5f, hr = roll * 0.5f;
    float cy = cosf(hy), sy = sinf(hy);
    float cp = cosf(hp), sp = sinf(hp);
    float cr = cosf(hr), sr = sinf(hr);
    out->x = cy*sp*cr + sy*cp*sr;
    out->y = sy*cp*cr - cy*sp*sr;
    out->z = cy*cp*sr - sy*sp*cr;
    out->w = cy*cp*cr + sy*sp*sr;
    return out;
}

D3DXQUATERNION* WINAPI D3DXQuaternionRotationAxis(D3DXQUATERNION* out, const D3DXVECTOR3* axis, float angle)
{
    D3DXVECTOR3 n;
    D3DXVec3Normalize(&n, axis);
    float h = angle * 0.5f, s = sinf(h);
    out->x = n.x*s; out->y = n.y*s; out->z = n.z*s; out->w = cosf(h);
    return out;
}

D3DXQUATERNION* WINAPI D3DXQuaternionMultiply(D3DXQUATERNION* out, const D3DXQUATERNION* a, const D3DXQUATERNION* b)
{
    // D3DX order: the result applies a's rotation then b's.
    D3DXQUATERNION r;
    r.x = b->w*a->x + b->x*a->w + b->y*a->z - b->z*a->y;
    r.y = b->w*a->y - b->x*a->z + b->y*a->w + b->z*a->x;
    r.z = b->w*a->z + b->x*a->y - b->y*a->x + b->z*a->w;
    r.w = b->w*a->w - b->x*a->x - b->y*a->y - b->z*a->z;
    *out = r;
    return out;
}

D3DXQUATERNION* WINAPI D3DXQuaternionNormalize(D3DXQUATERNION* out, const D3DXQUATERNION* in)
{
    float len = sqrtf(in->x*in->x + in->y*in->y + in->z*in->z + in->w*in->w);
    if (len > 0) { *out = { in->x/len, in->y/len, in->z/len, in->w/len }; }
    else { *out = { 0, 0, 0, 1 }; }
    return out;
}

D3DXQUATERNION* WINAPI D3DXQuaternionSlerp(D3DXQUATERNION* out, const D3DXQUATERNION* a, const D3DXQUATERNION* b, float t)
{
    float dot = a->x*b->x + a->y*b->y + a->z*b->z + a->w*b->w;
    D3DXQUATERNION end = *b;
    if (dot < 0.0f) { dot = -dot; end = { -b->x, -b->y, -b->z, -b->w }; }
    float k0, k1;
    if (dot > 0.9995f) { k0 = 1.0f - t; k1 = t; }
    else
    {
        float theta = acosf(dot), sinTheta = sinf(theta);
        k0 = sinf((1.0f - t) * theta) / sinTheta;
        k1 = sinf(t * theta) / sinTheta;
    }
    out->x = a->x*k0 + end.x*k1;
    out->y = a->y*k0 + end.y*k1;
    out->z = a->z*k0 + end.z*k1;
    out->w = a->w*k0 + end.w*k1;
    return out;
}

// -------------------------------------------------------------------- planes

D3DXPLANE* WINAPI D3DXPlaneNormalize(D3DXPLANE* out, const D3DXPLANE* in)
{
    float len = sqrtf(in->a*in->a + in->b*in->b + in->c*in->c);
    if (len > 0) { *out = { in->a/len, in->b/len, in->c/len, in->d/len }; }
    else { *out = { 0, 0, 0, 0 }; }
    return out;
}

D3DXPLANE* WINAPI D3DXPlaneFromPointNormal(D3DXPLANE* out, const D3DXVECTOR3* p, const D3DXVECTOR3* n)
{
    out->a = n->x; out->b = n->y; out->c = n->z;
    out->d = -(n->x*p->x + n->y*p->y + n->z*p->z);
    return out;
}

BOOL WINAPI DllMain(HINSTANCE, DWORD, LPVOID) { return TRUE; }
