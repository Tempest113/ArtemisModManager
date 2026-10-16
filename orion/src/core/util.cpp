#include "util.h"

#include <algorithm>
#include <cstdio>
#include <cstring>

namespace orion {

std::wstring Widen(const std::string& s) {
  if (s.empty()) return {};
  const int n = MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), nullptr, 0);
  std::wstring w(static_cast<size_t>(n), L'\0');
  MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), w.data(), n);
  return w;
}

std::string Narrow(const std::wstring& w) {
  if (w.empty()) return {};
  const int n = WideCharToMultiByte(CP_UTF8, 0, w.data(), static_cast<int>(w.size()), nullptr, 0, nullptr, nullptr);
  std::string s(static_cast<size_t>(n), '\0');
  WideCharToMultiByte(CP_UTF8, 0, w.data(), static_cast<int>(w.size()), s.data(), n, nullptr, nullptr);
  return s;
}

std::string Lower(std::string s) {
  for (char& c : s)
    if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
  return s;
}

bool EqualNoCase(const std::string& a, const std::string& b) { return Lower(a) == Lower(b); }

std::wstring Env(const wchar_t* name) {
  const DWORD n = GetEnvironmentVariableW(name, nullptr, 0);
  if (n == 0) return {};
  std::wstring v(n, L'\0');
  const DWORD got = GetEnvironmentVariableW(name, v.data(), n);
  v.resize(got);
  return v;
}

std::wstring Join(const std::wstring& dir, const std::wstring& name) {
  if (dir.empty()) return name;
  return (dir.back() == L'\\' || dir.back() == L'/') ? dir + name : dir + L'\\' + name;
}

std::wstring WithBackslashes(std::wstring path) {
  for (wchar_t& c : path)
    if (c == L'/') c = L'\\';
  return path;
}

bool FileExists(const std::wstring& path) {
  const DWORD a = GetFileAttributesW(path.c_str());
  return a != INVALID_FILE_ATTRIBUTES && !(a & FILE_ATTRIBUTE_DIRECTORY);
}

bool EnsureDir(const std::wstring& path) {
  if (path.empty()) return false;
  const DWORD a = GetFileAttributesW(path.c_str());
  if (a != INVALID_FILE_ATTRIBUTES) return (a & FILE_ATTRIBUTE_DIRECTORY) != 0;
  const size_t cut = path.find_last_of(L"\\/");
  if (cut != std::wstring::npos && cut > 2 && !EnsureDir(path.substr(0, cut))) return false;
  return CreateDirectoryW(path.c_str(), nullptr) || GetLastError() == ERROR_ALREADY_EXISTS;
}

bool ReadHandle(HANDLE file, std::string& out) {
  LARGE_INTEGER size;
  if (!GetFileSizeEx(file, &size) || size.QuadPart > (256ll << 20)) return false;
  out.assign(static_cast<size_t>(size.QuadPart), '\0');
  size_t done = 0;
  while (done < out.size()) {
    DWORD got = 0;
    if (!ReadFile(file, out.data() + done, static_cast<DWORD>(std::min<size_t>(out.size() - done, 1u << 20)), &got, nullptr) || got == 0) return false;
    done += got;
  }
  return true;
}

bool ReadAll(const std::wstring& path, std::string& out) {
  const HANDLE h = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
                               FILE_ATTRIBUTE_NORMAL, nullptr);
  if (h == INVALID_HANDLE_VALUE) return false;
  const bool ok = ReadHandle(h, out);
  CloseHandle(h);
  return ok;
}

bool WriteAtomic(const std::wstring& path, const std::string& data) {
  const std::wstring temp = path + L".tmp" + std::to_wstring(GetCurrentProcessId());
  const HANDLE h = CreateFileW(temp.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
  if (h == INVALID_HANDLE_VALUE) return false;
  size_t done = 0;
  bool ok = true;
  while (ok && done < data.size()) {
    DWORD put = 0;
    ok = WriteFile(h, data.data() + done, static_cast<DWORD>(std::min<size_t>(data.size() - done, 1u << 20)), &put, nullptr) && put > 0;
    done += put;
  }
  ok = CloseHandle(h) && ok;
  if (ok && MoveFileExW(temp.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) return true;
  DeleteFileW(temp.c_str());
  return false;
}

namespace {

constexpr uint32_t K[64] = {
    0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5, 0xd807aa98, 0x12835b01, 0x243185be,
    0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174, 0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa,
    0x5cb0a9dc, 0x76f988da, 0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967, 0x27b70a85,
    0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85, 0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3,
    0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070, 0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f,
    0x682e6ff3, 0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2};

inline uint32_t Rotr(uint32_t x, int n) { return (x >> n) | (x << (32 - n)); }

void Block(uint32_t h[8], const unsigned char* p) {
  uint32_t w[64];
  for (int i = 0; i < 16; ++i) w[i] = (uint32_t(p[4 * i]) << 24) | (uint32_t(p[4 * i + 1]) << 16) | (uint32_t(p[4 * i + 2]) << 8) | p[4 * i + 3];
  for (int i = 16; i < 64; ++i) {
    const uint32_t s0 = Rotr(w[i - 15], 7) ^ Rotr(w[i - 15], 18) ^ (w[i - 15] >> 3);
    const uint32_t s1 = Rotr(w[i - 2], 17) ^ Rotr(w[i - 2], 19) ^ (w[i - 2] >> 10);
    w[i] = w[i - 16] + s0 + w[i - 7] + s1;
  }
  uint32_t a = h[0], b = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], hh = h[7];
  for (int i = 0; i < 64; ++i) {
    const uint32_t t1 = hh + (Rotr(e, 6) ^ Rotr(e, 11) ^ Rotr(e, 25)) + ((e & f) ^ (~e & g)) + K[i] + w[i];
    const uint32_t t2 = (Rotr(a, 2) ^ Rotr(a, 13) ^ Rotr(a, 22)) + ((a & b) ^ (a & c) ^ (b & c));
    hh = g; g = f; f = e; e = d + t1; d = c; c = b; b = a; a = t1 + t2;
  }
  h[0] += a; h[1] += b; h[2] += c; h[3] += d; h[4] += e; h[5] += f; h[6] += g; h[7] += hh;
}

}  // namespace

// Written out here rather than taken from the system's crypto library: it needs no extra DLL to be loaded while Windows is still
// starting the game, and the tests check it against Python's hashlib on the same bytes.
std::string Sha256Hex(const std::string& bytes) {
  uint32_t h[8] = {0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19};
  const auto* data = reinterpret_cast<const unsigned char*>(bytes.data());
  const size_t n = bytes.size();
  size_t i = 0;
  for (; i + 64 <= n; i += 64) Block(h, data + i);
  unsigned char tail[128] = {};
  const size_t rest = n - i;
  if (rest) memcpy(tail, data + i, rest);
  tail[rest] = 0x80;
  const size_t total = rest + 1 + 8 <= 64 ? 64 : 128;
  const uint64_t bits = static_cast<uint64_t>(n) * 8;
  for (int k = 0; k < 8; ++k) tail[total - 1 - k] = static_cast<unsigned char>(bits >> (8 * k));
  Block(h, tail);
  if (total == 128) Block(h, tail + 64);
  char hex[65];
  for (int k = 0; k < 8; ++k) snprintf(hex + 8 * k, 9, "%08x", h[k]);
  return std::string(hex, 64);
}

std::string NowIsoUtc() {
  SYSTEMTIME t;
  GetSystemTime(&t);
  char buf[32];
  snprintf(buf, sizeof(buf), "%04d-%02d-%02dT%02d:%02d:%02dZ", t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond);
  return buf;
}

Log::~Log() {
  if (file_ != INVALID_HANDLE_VALUE) CloseHandle(file_);
}

// Every line starts "[HH:MM:SS]", which is what the manager's Logs page uses to show one line per entry.
void Log::Write(const char* level, const std::string& message) {
  if (path_.empty()) return;
  AcquireSRWLockExclusive(&lock_);
  if (file_ == INVALID_HANDLE_VALUE && !failed_) {
    file_ = CreateFileW(path_.c_str(), GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    failed_ = file_ == INVALID_HANDLE_VALUE;
  }
  if (file_ != INVALID_HANDLE_VALUE) {
    SYSTEMTIME t;
    GetLocalTime(&t);
    char head[48];
    snprintf(head, sizeof(head), "[%02d:%02d:%02d] %s: ", t.wHour, t.wMinute, t.wSecond, level);
    std::string line = head + message + "\r\n";
    DWORD put = 0;
    WriteFile(file_, line.data(), static_cast<DWORD>(line.size()), &put, nullptr);
  }
  ReleaseSRWLockExclusive(&lock_);
}

}  // namespace orion
