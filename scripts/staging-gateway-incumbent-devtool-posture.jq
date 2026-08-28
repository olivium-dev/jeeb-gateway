def env_pair: capture("^(?<key>[^=]+)=(?<value>.*)$");
def boolean_setting($key; $default):
  [
    (.TaskTemplate.ContainerSpec.Env // [])[]
    | env_pair
    | select((.key | ascii_downcase) == ($key | ascii_downcase))
    | .value
  ] as $values
  | if ($values | length) == 0 then $default
    elif ($values | length) == 1 then
      ($values[0] | ascii_downcase) as $normalized
      | if $normalized == "true" then true
        elif $normalized == "false" then false
        else error("incumbent boolean is invalid: " + $key)
        end
    else error("incumbent boolean is duplicate: " + $key)
    end;

[
  boolean_setting("SuperLogin__OpenMode"; false),
  boolean_setting("DemoUsers__Enabled"; true),
  boolean_setting("Features__DevEndpoints__Enabled"; false),
  boolean_setting("Features__Swagger__Enabled"; false),
  boolean_setting("Security__TokenMint__Enabled"; true)
]
| map(tostring)
| @tsv
