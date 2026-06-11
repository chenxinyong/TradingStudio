#pragma once

#include <msclr/marshal_cppstd.h>
#include <string>

using namespace System;

namespace CTP
{
    /// String conversion utilities for C++/CLI boundary
    public ref class StringHelper
    {
    public:
        static std::string ToNative(String^ str)
        {
            return msclr::interop::marshal_as<std::string>(str);
        }

        static String^ ToManaged(const char* str)
        {
            if (!str) return String::Empty;
            return gcnew String(str);
        }

        /// Copy managed string into fixed-size C char array (safe)
        static void CopyToBuffer(String^ str, char* buf, size_t bufSize)
        {
            if (!buf || bufSize == 0) return;
            memset(buf, 0, bufSize);
            if (String::IsNullOrEmpty(str)) return;
            auto native = ToNative(str);
            strncpy_s(buf, bufSize, native.c_str(), min(native.size(), bufSize - 1));
        }
    };
}
