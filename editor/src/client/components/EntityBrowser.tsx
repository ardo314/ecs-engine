import { useState } from "react";
import {
  Accordion,
  ActionIcon,
  Badge,
  Button,
  Code,
  Group,
  ScrollArea,
  Table,
  Text,
  TextInput,
  Title,
  Tooltip,
} from "@mantine/core";
import type { EntitySnapshot } from "../types";

interface EntityBrowserProps {
  entities: EntitySnapshot[];
  onCreateEntity: () => void;
  onDeleteEntity: (entityId: number) => void;
  onRemoveComponent: (entityId: number, componentType: string) => void;
}

export function EntityBrowser({
  entities,
  onCreateEntity,
  onDeleteEntity,
  onRemoveComponent,
}: EntityBrowserProps) {
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
      <Group justify="space-between" mb="sm">
        <Title order={3}>Entities</Title>
        <Button size="xs" onClick={onCreateEntity}>
          + Create Entity
        </Button>
      </Group>

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
                  <Group gap="sm" wrap="nowrap">
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
                  <ComponentDetails
                    entity={entity}
                    onRemoveComponent={(type) =>
                      onRemoveComponent(entity.entityId, type)
                    }
                  />
                  <Group mt="xs">
                    <Button
                      size="xs"
                      variant="light"
                      color="red"
                      onClick={() => onDeleteEntity(entity.entityId)}
                    >
                      Delete Entity
                    </Button>
                  </Group>
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
  entity,
  onRemoveComponent,
}: {
  entity: EntitySnapshot;
  onRemoveComponent: (type: string) => void;
}) {
  return (
    <Table withTableBorder withColumnBorders>
      <Table.Thead>
        <Table.Tr>
          <Table.Th>Component</Table.Th>
          <Table.Th>Fields</Table.Th>
          <Table.Th w={40} />
        </Table.Tr>
      </Table.Thead>
      <Table.Tbody>
        {Object.entries(entity.components).map(([type, fields]) => (
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
            <Table.Td>
              <Tooltip label={`Remove ${type}`}>
                <ActionIcon
                  size="sm"
                  variant="subtle"
                  color="red"
                  onClick={() => onRemoveComponent(type)}
                >
                  ✕
                </ActionIcon>
              </Tooltip>
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
