// The D3DX9 maths types and the exported function set the shim implements.
//
// D3DX was removed from Windows, so there is nothing to forward to: this is a
// real implementation. The types and calling convention are the published
// D3DX9 ABI (row-major matrices, row-vector convention v' = v*M, __stdcall
// exports returning their out pointer) so a game linked against d3dx9.lib finds
// each function by name with the layout it expects. Only the non-inline
// functions are exported; the ones the D3DX headers define inline are compiled
// into the game and never imported.

#pragma once

#include <windows.h>

#ifdef D3DX9_EXPORTS
#define D3DX9API extern "C" __declspec(dllexport)
#else
#define D3DX9API extern "C"
#endif

typedef struct D3DXVECTOR2 { float x, y; } D3DXVECTOR2;
typedef struct D3DXVECTOR3 { float x, y, z; } D3DXVECTOR3;
typedef struct D3DXVECTOR4 { float x, y, z, w; } D3DXVECTOR4;
typedef struct D3DXQUATERNION { float x, y, z, w; } D3DXQUATERNION;
typedef struct D3DXPLANE { float a, b, c, d; } D3DXPLANE;

typedef struct D3DXMATRIX
{
    union
    {
        struct
        {
            float _11, _12, _13, _14;
            float _21, _22, _23, _24;
            float _31, _32, _33, _34;
            float _41, _42, _43, _44;
        };
        float m[4][4];
    };
} D3DXMATRIX;

// --------------------------------------------------------------- matrices

D3DX9API D3DXMATRIX* WINAPI D3DXMatrixMultiply(D3DXMATRIX* out, const D3DXMATRIX* a, const D3DXMATRIX* b);
D3DX9API D3DXMATRIX* WINAPI D3DXMatrixMultiplyTranspose(D3DXMATRIX* out, const D3DXMATRIX* a, const D3DXMATRIX* b);
D3DX9API D3DXMATRIX* WINAPI D3DXMatrixTranspose(D3DXMATRIX* out, const D3DXMATRIX* in);
D3DX9API float       WINAPI D3DXMatrixDeterminant(const D3DXMATRIX* in);
D3DX9API D3DXMATRIX* WINAPI D3DXMatrixInverse(D3DXMATRIX* out, float* determinant, const D3DXMATRIX* in);
D3DX9API D3DXMATRIX* WINAPI D3DXMatrixTranslation(D3DXMATRIX* out, float x, float y, float z);
D3DX9API D3DXMATRIX* WINAPI D3DXMatrixScaling(D3DXMATRIX* out, float x, float y, float z);
D3DX9API D3DXMATRIX* WINAPI D3DXMatrixRotationX(D3DXMATRIX* out, float angle);
D3DX9API D3DXMATRIX* WINAPI D3DXMatrixRotationY(D3DXMATRIX* out, float angle);
D3DX9API D3DXMATRIX* WINAPI D3DXMatrixRotationZ(D3DXMATRIX* out, float angle);
D3DX9API D3DXMATRIX* WINAPI D3DXMatrixRotationAxis(D3DXMATRIX* out, const D3DXVECTOR3* axis, float angle);
D3DX9API D3DXMATRIX* WINAPI D3DXMatrixRotationQuaternion(D3DXMATRIX* out, const D3DXQUATERNION* q);
D3DX9API D3DXMATRIX* WINAPI D3DXMatrixRotationYawPitchRoll(D3DXMATRIX* out, float yaw, float pitch, float roll);
D3DX9API D3DXMATRIX* WINAPI D3DXMatrixLookAtLH(D3DXMATRIX* out, const D3DXVECTOR3* eye, const D3DXVECTOR3* at, const D3DXVECTOR3* up);
D3DX9API D3DXMATRIX* WINAPI D3DXMatrixLookAtRH(D3DXMATRIX* out, const D3DXVECTOR3* eye, const D3DXVECTOR3* at, const D3DXVECTOR3* up);
D3DX9API D3DXMATRIX* WINAPI D3DXMatrixPerspectiveFovLH(D3DXMATRIX* out, float fovY, float aspect, float zn, float zf);
D3DX9API D3DXMATRIX* WINAPI D3DXMatrixPerspectiveFovRH(D3DXMATRIX* out, float fovY, float aspect, float zn, float zf);
D3DX9API D3DXMATRIX* WINAPI D3DXMatrixOrthoLH(D3DXMATRIX* out, float w, float h, float zn, float zf);
D3DX9API D3DXMATRIX* WINAPI D3DXMatrixOrthoRH(D3DXMATRIX* out, float w, float h, float zn, float zf);
D3DX9API D3DXMATRIX* WINAPI D3DXMatrixOrthoOffCenterLH(D3DXMATRIX* out, float l, float r, float b, float t, float zn, float zf);

// ---------------------------------------------------------------- vectors

D3DX9API D3DXVECTOR3* WINAPI D3DXVec3Normalize(D3DXVECTOR3* out, const D3DXVECTOR3* in);
D3DX9API D3DXVECTOR4* WINAPI D3DXVec3Transform(D3DXVECTOR4* out, const D3DXVECTOR3* in, const D3DXMATRIX* m);
D3DX9API D3DXVECTOR3* WINAPI D3DXVec3TransformCoord(D3DXVECTOR3* out, const D3DXVECTOR3* in, const D3DXMATRIX* m);
D3DX9API D3DXVECTOR3* WINAPI D3DXVec3TransformNormal(D3DXVECTOR3* out, const D3DXVECTOR3* in, const D3DXMATRIX* m);
D3DX9API D3DXVECTOR4* WINAPI D3DXVec4Transform(D3DXVECTOR4* out, const D3DXVECTOR4* in, const D3DXMATRIX* m);
D3DX9API D3DXVECTOR4* WINAPI D3DXVec4Normalize(D3DXVECTOR4* out, const D3DXVECTOR4* in);
D3DX9API D3DXVECTOR2* WINAPI D3DXVec2Normalize(D3DXVECTOR2* out, const D3DXVECTOR2* in);

// ------------------------------------------------------------ quaternions

D3DX9API D3DXQUATERNION* WINAPI D3DXQuaternionRotationMatrix(D3DXQUATERNION* out, const D3DXMATRIX* m);
D3DX9API D3DXQUATERNION* WINAPI D3DXQuaternionRotationYawPitchRoll(D3DXQUATERNION* out, float yaw, float pitch, float roll);
D3DX9API D3DXQUATERNION* WINAPI D3DXQuaternionRotationAxis(D3DXQUATERNION* out, const D3DXVECTOR3* axis, float angle);
D3DX9API D3DXQUATERNION* WINAPI D3DXQuaternionMultiply(D3DXQUATERNION* out, const D3DXQUATERNION* a, const D3DXQUATERNION* b);
D3DX9API D3DXQUATERNION* WINAPI D3DXQuaternionNormalize(D3DXQUATERNION* out, const D3DXQUATERNION* in);
D3DX9API D3DXQUATERNION* WINAPI D3DXQuaternionSlerp(D3DXQUATERNION* out, const D3DXQUATERNION* a, const D3DXQUATERNION* b, float t);

// ---------------------------------------------------------------- planes

D3DX9API D3DXPLANE* WINAPI D3DXPlaneNormalize(D3DXPLANE* out, const D3DXPLANE* in);
D3DX9API D3DXPLANE* WINAPI D3DXPlaneFromPointNormal(D3DXPLANE* out, const D3DXVECTOR3* point, const D3DXVECTOR3* normal);
