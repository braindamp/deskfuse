#pragma once

#pragma warning(push, 0)
#include <nlohmann/json.hpp>
#pragma warning(pop)

#include <string>

using Json = nlohmann::ordered_json;

inline std::string DumpJson(const Json& value)
{
    return value.dump(2);
}
