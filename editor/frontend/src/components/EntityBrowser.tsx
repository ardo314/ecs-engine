import { useState } from "react";
import {
  Accordion,
  Badge,
  Code,
  Group,
  ScrollArea,
  Table,
  Text,
  TextInput,
  Title,
} from "@mantine/core";
import type { EntitySnapshot } from "../types";

interface EntityBrowserProps {
  entities: EntitySnapshot[];
}

export function EntityBrowser({ entities }: EntityBrowserProps) {
  const [filter, setFilter] = useState("");

  const lower = filter.toLowerCase();
  const filtered = entities.filter((e) => {
    if (!filter) return true;
    if (String(e.entityId).includes(lower)) return true;
    return Object.keys(e.components).some((t) =>
      t.toLowerCase().includes(lower)
    );
  });

  return (
    <div>
      <Title order={3} mb="sm">
        Entities
      </Title>

      <TextInput
        placeholder="Filter by entity ID or component type..."
        value={filter}
        onChange={(e) => setFilter(e.currentTarget.value)}
        mb="sm"
        size="sm"
      />

      <Text size="xs" c="dimmed" mb="xs">
        {filtered.length} of {entities.length} entities
      </Text>

      <ScrollArea h="calc(100vh - 320px)">
        {filtered.length === 0 ? (
          <Text c="dimmed" size="sm">
            No entities
          </Text>
        ) : (
          <Accordion variant="separated">
            {filtered.map((entity) => (
              <Accordion.Item
                key={entity.entityId}
                value={String(entity.entityId)}
              >
                <Accordion.Control>
                  <Group gap="sm">
                    <Text fw={500} size="sm">
                      Entity {entity.entityId}
                    </Text>
                    {Object.keys(entity.components).map((type) => (
                      <Badge key={type} size="xs" variant="dot">
                        {type}
                      </Badge>
                    ))}
                  </Group>
                </Accordion.Control>
                <Accordion.Panel>
                  <ComponentDetails components={entity.components} />
                </Accordion.Panel>
              </Accordion.Item>
            ))}
          </Accordion>
        )}
      </ScrollArea>
    </div>
  );
}

function ComponentDetails({
  components,
}: {
  components: Record<string, Record<string, unknown> | null>;
}) {
  return (
    <Table withTableBorder withColumnBorders>
      <Table.Thead>
        <Table.Tr>
          <Table.Th>Component</Table.Th>
          <Table.Th>Fields</Table.Th>
        </Table.Tr>
      </Table.Thead>
      <Table.Tbody>
        {Object.entries(components).map(([type, fields]) => (
          <Table.Tr key={type}>
            <Table.Td>
              <Text fw={500} size="sm">
                {type}
              </Text>
            </Table.Td>
            <Table.Td>
              {fields ? (
                <Group gap="xs">
                  {Object.entries(fields).map(([key, val]) => (
                    <Code key={key}>
                      {key}: {formatValue(val)}
                    </Code>
                  ))}
                </Group>
              ) : (
                <Text c="dimmed" size="sm">
                  (unable to deserialize)
                </Text>
              )}
            </Table.Td>
          </Table.Tr>
        ))}
      </Table.Tbody>
    </Table>
  );
}

function formatValue(val: unknown): string {
  if (typeof val === "number") {
    return Number.isInteger(val) ? String(val) : val.toFixed(3);
  }
  return JSON.stringify(val);
}
