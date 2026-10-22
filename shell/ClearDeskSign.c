// ClearDeskSign.exe <package.msix> <sha1-thumbprint>
// Signs an MSIX with a certificate from CurrentUser\My using the in-box
// SignerSignEx2 + APPX SIP (no Windows SDK / signtool needed on end-user PCs).
#include <windows.h>
#include <wincrypt.h>
#include <stdio.h>
#pragma comment(lib, "crypt32.lib")

typedef struct { DWORD cbSize; LPCWSTR pwszFileName; HANDLE hFile; } SIGNER_FILE_INFO;
typedef struct { DWORD cbSize; DWORD *pdwIndex; DWORD dwSubjectChoice; union { SIGNER_FILE_INFO *pSignerFileInfo; void *pSignerBlobInfo; }; } SIGNER_SUBJECT_INFO;
typedef struct { DWORD cbSize; PCCERT_CONTEXT pSigningCert; DWORD dwCertPolicy; HCERTSTORE hCertStore; } SIGNER_CERT_STORE_INFO;
typedef struct { DWORD cbSize; DWORD dwCertChoice; union { LPCWSTR pwszSpcFile; SIGNER_CERT_STORE_INFO *pCertStoreInfo; void *pSpcChainInfo; }; HWND hwnd; } SIGNER_CERT;
typedef struct { DWORD cbSize; BOOL fCommercial; BOOL fIndividual; LPCWSTR pwszName; LPCWSTR pwszInfo; } SIGNER_ATTR_AUTHCODE;
typedef struct { DWORD cbSize; ALG_ID algidHash; DWORD dwAttrChoice; union { SIGNER_ATTR_AUTHCODE *pAttrAuthcode; }; PCRYPT_ATTRIBUTES psAuthenticated; PCRYPT_ATTRIBUTES psUnauthenticated; } SIGNER_SIGNATURE_INFO;
typedef struct { DWORD cbSize; DWORD cbBlob; BYTE *pbBlob; } SIGNER_CONTEXT;
typedef struct {
    DWORD dwFlags; SIGNER_SUBJECT_INFO *pSubjectInfo; SIGNER_CERT *pSigningCert; SIGNER_SIGNATURE_INFO *pSignatureInfo;
    void *pProviderInfo; DWORD dwTimestampFlags; PCSTR pszAlgorithmOid; PCWSTR pwszTimestampURL; PCRYPT_ATTRIBUTES pCryptAttrs;
    PVOID pSipData; SIGNER_CONTEXT **pSignerContext; PVOID pCryptoPolicy; PVOID pReserved;
} SIGNER_SIGN_EX2_PARAMS;
typedef struct { SIGNER_SIGN_EX2_PARAMS *pSignerParams; IUnknown *pAppxSipState; } APPX_SIP_CLIENT_DATA;
typedef HRESULT (WINAPI *SignerSignEx2Fn)(DWORD, SIGNER_SUBJECT_INFO *, SIGNER_CERT *, SIGNER_SIGNATURE_INFO *, void *, DWORD, PCSTR, PCWSTR, PCRYPT_ATTRIBUTES, PVOID, SIGNER_CONTEXT **, PVOID, PVOID);
typedef HRESULT (WINAPI *SignerFreeFn)(SIGNER_CONTEXT *);

static int hexval(wchar_t c) { if (c >= L'0' && c <= L'9') return c - L'0'; c |= 0x20; if (c >= L'a' && c <= L'f') return c - L'a' + 10; return -1; }

int wmain(int argc, wchar_t **argv)
{
    if (argc != 3) { fwprintf(stderr, L"usage: ClearDeskSign <package.msix> <thumbprint>\n"); return 2; }
    BYTE hash[20]; int n = 0;
    for (const wchar_t *p = argv[2]; *p && n < 40; p++) { int v = hexval(*p); if (v < 0) continue; if (n % 2 == 0) hash[n / 2] = (BYTE)(v << 4); else hash[n / 2] |= (BYTE)v; n++; }
    if (n != 40) { fwprintf(stderr, L"bad thumbprint\n"); return 2; }
    HCERTSTORE store = CertOpenStore(CERT_STORE_PROV_SYSTEM_W, 0, 0, CERT_SYSTEM_STORE_CURRENT_USER | CERT_STORE_OPEN_EXISTING_FLAG | CERT_STORE_READONLY_FLAG, L"My");
    if (!store) { fwprintf(stderr, L"open store failed %lu\n", GetLastError()); return 3; }
    CRYPT_HASH_BLOB blob = { 20, hash };
    PCCERT_CONTEXT cert = CertFindCertificateInStore(store, X509_ASN_ENCODING | PKCS_7_ASN_ENCODING, 0, CERT_FIND_SHA1_HASH, &blob, NULL);
    if (!cert) { fwprintf(stderr, L"certificate not found\n"); return 3; }
    HMODULE mssign = LoadLibraryExW(L"mssign32.dll", NULL, LOAD_LIBRARY_SEARCH_SYSTEM32);
    SignerSignEx2Fn signEx2 = mssign ? (SignerSignEx2Fn)GetProcAddress(mssign, "SignerSignEx2") : NULL;
    SignerFreeFn freeCtx = mssign ? (SignerFreeFn)GetProcAddress(mssign, "SignerFreeSignerContext") : NULL;
    if (!signEx2) { fwprintf(stderr, L"SignerSignEx2 unavailable\n"); return 4; }

    DWORD index = 0;
    SIGNER_FILE_INFO file = { sizeof(file), argv[1], NULL };
    SIGNER_SUBJECT_INFO subject = { sizeof(subject), &index, 1 /*SIGNER_SUBJECT_FILE*/ }; subject.pSignerFileInfo = &file;
    SIGNER_CERT_STORE_INFO storeInfo = { sizeof(storeInfo), cert, 2 /*SIGNER_CERT_POLICY_CHAIN*/, NULL };
    SIGNER_CERT signer = { sizeof(signer), 2 /*SIGNER_CERT_STORE*/ }; signer.pCertStoreInfo = &storeInfo;
    SIGNER_ATTR_AUTHCODE authcode = { sizeof(authcode), FALSE, TRUE, L"", L"" };
    SIGNER_SIGNATURE_INFO sig = { sizeof(sig), CALG_SHA_256, 1 /*SIGNER_AUTHCODE_ATTR*/ }; sig.pAttrAuthcode = &authcode;
    SIGNER_CONTEXT *context = NULL;
    SIGNER_SIGN_EX2_PARAMS params = { 0 };
    params.pSubjectInfo = &subject; params.pSigningCert = &signer; params.pSignatureInfo = &sig; params.pSignerContext = &context;
    APPX_SIP_CLIENT_DATA sip = { &params, NULL };
    params.pSipData = &sip;
    HRESULT hr = signEx2(0, &subject, &signer, &sig, NULL, 0, NULL, NULL, NULL, &sip, &context, NULL, NULL);
    if (sip.pAppxSipState) sip.pAppxSipState->lpVtbl->Release(sip.pAppxSipState);
    if (context && freeCtx) freeCtx(context);
    CertFreeCertificateContext(cert); CertCloseStore(store, 0);
    if (FAILED(hr)) { fwprintf(stderr, L"SignerSignEx2 failed 0x%08lX\n", (unsigned long)hr); return 5; }
    wprintf(L"signed\n");
    return 0;
}
