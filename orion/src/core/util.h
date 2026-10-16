// Small Windows helpers for the core: text conversion, whole-file reads and atomic writes, SHA-256, the log.
#pragma once

#include <windows.h>

#include <cstdint>
#include <string>

namespace orion {

std::wstring Widen(const std::string& utf8);
std::string Narrow(const std::wstring& wide);
std::string Lower(std::string s);
bool EqualNoCase(const std::string& a, const std::string& b);

// "" if the variable is not set.
std::wstring Env(const wchar_t* name);
std::wstring Join(const std::wstring& dir, const std::wstring& name);
std::wstring WithBackslashes(std::wstring path);

bool FileExists(const std::wstring& path);
bool EnsureDir(const std::wstring& path);
// Reads a whole file (capped at 256 MB). Others may keep the file open.
bool ReadAll(const std::wstring& path, std::string& out);
bool ReadHandle(HANDLE file, std::string& out);
// Writes to a temp file next to the target and moves it over, so a reader never sees half a file.
bool WriteAtomic(const std::wstring& path, const std::string& data);

// Lower-case hex SHA-256 of the bytes.
std::string Sha256Hex(const std::string& bytes);

// 2026-10-15T12:00:00Z
std::string NowIsoUtc();

// A log file, opened (and the old one cut off) at the first line. Safe to write to from several threads.
class Log {
 public:
  explicit Log(std::wstring path = L"") : path_(std::move(path)) { InitializeSRWLock(&lock_); }
  Log(const Log&) = delete;
  Log& operator=(const Log&) = delete;
  ~Log();
  void SetPath(std::wstring path) { path_ = std::move(path); }
  void Write(const char* level, const std::string& message);

 private:
  std::wstring path_;
  HANDLE file_ = INVALID_HANDLE_VALUE;
  bool failed_ = false;
  SRWLOCK lock_;
};

}  // namespace orion
