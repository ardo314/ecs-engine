import { Badge, Grid, Group, Text, Title } from "@mantine/core";
import { useEngineSocket } from "./hooks/useEngineSocket";
import { useEditorApi } from "./hooks/useEditorApi";
import { SystemsPanel } from "./components/SystemsPanel";
import { EntityBrowser } from "./components/EntityBrowser";

const BACKEND_URL = (import.meta.env.VITE_EDITOR_BACKEND_URL || "http://localhost:5000").replace(/\/+$/, "");
const WS_URL = BACKEND_URL.replace(/^http/, "ws") + "/ws";

export function App() {
  const { snapshot, connected } = useEngineSocket(WS_URL);
  const { createEntity, deleteEntity, removeComponent } = useEditorApi();

  return (
    <div style={{ padding: "1rem" }}>
      <Group justify="space-between" mb="md">
        <Title order={2}>Engine Editor</Title>
        <Group gap="sm">
          {snapshot && (
            <Text size="sm" c="dimmed">
              Tick {snapshot.tickId}
            </Text>
          )}
          <Badge
            color={connected ? "green" : "red"}
            variant="filled"
            size="sm"
          >
            {connected ? "Connected" : "Disconnected"}
          </Badge>
        </Group>
      </Group>

      <Grid>
        <Grid.Col span={5}>
          <SystemsPanel
            systems={snapshot?.systems ?? []}
            stages={snapshot?.stages ?? []}
          />
        </Grid.Col>
        <Grid.Col span={7}>
          <EntityBrowser
            entities={snapshot?.entities ?? []}
            onCreateEntity={createEntity}
            onDeleteEntity={deleteEntity}
            onRemoveComponent={removeComponent}
          />
        </Grid.Col>
      </Grid>
    </div>
  );
}
