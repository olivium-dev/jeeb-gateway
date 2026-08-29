def env_key: split("=")[0] | ascii_downcase;
def is_devtool_flag:
  env_key as $key
  | [
      "superlogin__openmode",
      "demousers__enabled",
      "features__devendpoints__enabled",
      "features__swagger__enabled"
    ]
  | index($key) != null;
def patch_devtool($document):
  $document
  | .TaskTemplate.ContainerSpec.Image = $image
  | .TaskTemplate.ContainerSpec.Env = (
      ((.TaskTemplate.ContainerSpec.Env // [])
        | map(select(is_devtool_flag | not)))
      + [
          "SuperLogin__OpenMode=true",
          "DemoUsers__Enabled=true",
          "Features__DevEndpoints__Enabled=true",
          "Features__Swagger__Enabled=true"
        ]
    )
  | .UpdateConfig = ((.UpdateConfig // {}) + {
      Parallelism:1,
      Monitor:20000000000,
      FailureAction:"pause",
      Order:"start-first"
    })
  | .RollbackConfig = ((.RollbackConfig // {}) + {
      Parallelism:1,
      Monitor:20000000000,
      FailureAction:"pause",
      Order:"start-first"
    });
def incumbent_devtool_keys:
  [
    ($incumbent[0].TaskTemplate.ContainerSpec.Env // [])[]
    | select(is_devtool_flag)
    | env_key
  ];

($incumbent | length) == 1
and (incumbent_devtool_keys | length) == (incumbent_devtool_keys | unique | length)
and . == patch_devtool($incumbent[0])
