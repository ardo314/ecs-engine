import { Badge, Card, Group, Table, Text, Title } from "@mantine/core";
import type { SystemInfo } from "../types";

interface SystemsPanelProps {
  systems: SystemInfo[];
  stages: string[][];
}

export function SystemsPanel({ systems, stages }: SystemsPanelProps) {
  const systemMap = new Map(systems.map((s) => [s.name, s]));

  return (
    <div>
      <Title order={3} mb="sm">
        Systems
      </Title>

      {stages.length === 0 && (
        <Text c="dimmed" size="sm">
          No systems registered
        </Text>
      )}

      {stages.map((stageSystemNames, idx) => (
        <Card key={idx} withBorder mb="xs" padding="sm">
          <Text size="xs" c="dimmed" mb="xs">
            Stage {idx + 1}
            {stageSystemNames.length > 1 ? " (parallel)" : ""}
          </Text>

          <Table highlightOnHover withTableBorder={false} withColumnBorders={false}>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>System</Table.Th>
                <Table.Th>Reads</Table.Th>
                <Table.Th>Writes</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {stageSystemNames.map((name) => {
                const info = systemMap.get(name);
                if (!info) return null;
                return (
                  <Table.Tr key={name}>
                    <Table.Td>
                      <Text fw={500}>{name}</Text>
                    </Table.Td>
                    <Table.Td>
                      <Group gap={4}>
                        {info.reads.map((r) => (
                          <Badge key={r} size="sm" variant="light" color="blue">
                            {r}
                          </Badge>
                        ))}
                      </Group>
                    </Table.Td>
                    <Table.Td>
                      <Group gap={4}>
                        {info.writes.map((w) => (
                          <Badge key={w} size="sm" variant="light" color="orange">
                            {w}
                          </Badge>
                        ))}
                      </Group>
                    </Table.Td>
                  </Table.Tr>
                );
              })}
            </Table.Tbody>
          </Table>
        </Card>
      ))}
    </div>
  );
}
