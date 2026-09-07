def schema_refs:
  [.. | objects | .["$ref"]?
    | select(type == "string" and startswith("#/components/schemas/"))
    | split("/")[-1]]
  | unique;

def copy_schema_closure($candidate; $pending; $seen):
  if ($pending | length) == 0 then .
  else
    ($pending[0]) as $name
    | ($pending[1:]) as $remaining
    | if ($seen | index($name)) != null then
        copy_schema_closure($candidate; $remaining; $seen)
      else
        .components.schemas[$name] = $candidate.components.schemas[$name]
        | ($candidate.components.schemas[$name] | schema_refs) as $more
        | copy_schema_closure($candidate; (($remaining + $more) | unique); ($seen + [$name]))
      end
  end;

.[0] as $base
| .[1] as $candidate
| [
    "/form-builder/templates/{templateName}/submit",
    "/form-builder/templates/{templateName}/submission"
  ] as $selected_paths
| reduce $selected_paths[] as $path
    ($base; .paths[$path] = $candidate.paths[$path])
| ($selected_paths
    | map($candidate.paths[.] | schema_refs)
    | add
    | unique) as $root_schemas
| copy_schema_closure($candidate; $root_schemas; [])
