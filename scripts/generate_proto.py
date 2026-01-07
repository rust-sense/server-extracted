#!/usr/bin/env python3
"""
Generate .proto file from decompiled SilentOrbit.ProtocolBuffers C# classes.
Parses the Serialize/Deserialize methods to extract field numbers and types.
"""

import os
import re
import sys
from pathlib import Path
from collections import defaultdict
from dataclasses import dataclass, field
from typing import Optional


@dataclass
class ProtoField:
    number: int
    name: str
    proto_type: str
    is_repeated: bool = False
    is_optional: bool = True


@dataclass 
class ProtoEnum:
    name: str
    values: dict = field(default_factory=dict)


@dataclass
class ProtoMessage:
    name: str
    fields: list = field(default_factory=list)
    enums: list = field(default_factory=list)


# Mapping from C# types to proto types
CS_TO_PROTO_TYPE = {
    'int': 'int32',
    'uint': 'uint32', 
    'long': 'int64',
    'ulong': 'uint64',
    'float': 'float',
    'double': 'double',
    'bool': 'bool',
    'string': 'string',
    'byte[]': 'bytes',
    'Vector2': 'Vector2',
    'Vector3': 'Vector3',
    'Vector4': 'Vector4',
    'Color': 'Color',
    'Ray': 'Ray',
}


def parse_enum_file(filepath: Path) -> Optional[ProtoEnum]:
    """Parse a C# enum file and extract enum values."""
    content = filepath.read_text()
    
    # Match enum definition
    enum_match = re.search(r'public\s+enum\s+(\w+)\s*{([^}]+)}', content, re.DOTALL)
    if not enum_match:
        return None
    
    enum_name = enum_match.group(1)
    enum_body = enum_match.group(2)
    
    values = {}
    for line in enum_body.split('\n'):
        line = line.strip().rstrip(',')
        if not line or line.startswith('//'):
            continue
        
        # Match "Name = Value" or just "Name"
        match = re.match(r'(\w+)\s*(?:=\s*(\d+))?', line)
        if match:
            name = match.group(1)
            value = int(match.group(2)) if match.group(2) else len(values)
            values[name] = value
    
    if values:
        return ProtoEnum(name=enum_name, values=values)
    return None


def parse_proto_class(filepath: Path) -> Optional[ProtoMessage]:
    """Parse a C# proto class file and extract message structure."""
    content = filepath.read_text()
    
    # Check if it's a proto class (implements IProto)
    if 'IProto' not in content:
        return None
    
    # Get class name
    class_match = re.search(r'public\s+class\s+(\w+)\s*:', content)
    if not class_match:
        return None
    
    class_name = class_match.group(1)
    
    # Skip non-App classes for rustplus proto (unless it's a common type)
    if not class_name.startswith('App') and class_name not in [
        'Vector2', 'Vector3', 'Vector4', 'Color', 'Ray', 'ClanInfo', 'ClanLog',
        'ClanMember', 'ClanRole', 'ClanInvite', 'ClanMotd', 'PlayerNameID', 'Approval'
    ]:
        return None
    
    message = ProtoMessage(name=class_name)
    
    # Extract fields from NonSerialized attributes
    field_pattern = re.compile(
        r'\[NonSerialized\]\s*\n\s*public\s+(\w+(?:<[^>]+>)?(?:\[\])?)\s+(\w+);',
        re.MULTILINE
    )
    
    fields_found = {}
    for match in field_pattern.finditer(content):
        cs_type = match.group(1)
        field_name = match.group(2)
        
        # Skip internal fields
        if field_name in ['ShouldPool', '_disposed']:
            continue
            
        fields_found[field_name] = cs_type
    
    # Now find field numbers from Serialize method
    # The numbers in stream.Write*() are WIRE TAGS, not field numbers
    # Wire tag = (field_number << 3) | wire_type
    # Wire types: 0=varint, 1=64-bit, 2=length-delimited, 5=32-bit
    #
    # Look for patterns like: stream.WriteUInt64(8, instance.seq);  -> field 1 (8 >> 3 = 1)
    # or: stream.WriteLengthDelimited(10, instance.name);  -> field 1 (10 >> 3 = 1)
    serialize_pattern = re.compile(
        r'stream\.Write(?:LengthDelimited|With(?:LengthPrefix)?)?\w*\((\d+),\s*(?:instance\.)?(\w+)',
        re.MULTILINE
    )
    
    # Also look for Deserialize patterns: case 8: instance.seq = ...  -> field 1 (8 >> 3 = 1)
    deserialize_pattern = re.compile(
        r'case\s+(\d+):\s*\n?\s*(?:instance\.)?(\w+)\s*=',
        re.MULTILINE
    )
    
    field_numbers = {}
    
    for match in serialize_pattern.finditer(content):
        wire_tag = int(match.group(1))
        field_num = wire_tag >> 3  # Extract field number from wire tag
        field_name = match.group(2)
        if field_name in fields_found:
            field_numbers[field_name] = field_num
    
    for match in deserialize_pattern.finditer(content):
        wire_tag = int(match.group(1))
        field_num = wire_tag >> 3  # Extract field number from wire tag
        field_name = match.group(2)
        if field_name in fields_found and field_name not in field_numbers:
            field_numbers[field_name] = field_num
    
    # Build fields list
    for field_name, cs_type in fields_found.items():
        field_num = field_numbers.get(field_name, 0)
        if field_num == 0:
            continue  # Skip fields we couldn't find numbers for
        
        is_repeated = False
        proto_type = cs_type
        
        # Handle List<T>
        list_match = re.match(r'List<(\w+)>', cs_type)
        if list_match:
            is_repeated = True
            proto_type = list_match.group(1)
        
        # Handle arrays (but not byte[])
        if cs_type.endswith('[]'):
            if cs_type == 'byte[]':
                proto_type = 'bytes'
            else:
                is_repeated = True
                proto_type = cs_type[:-2]
        
        # Convert C# type to proto type
        proto_type = CS_TO_PROTO_TYPE.get(proto_type, proto_type)
        
        message.fields.append(ProtoField(
            number=field_num,
            name=field_name,
            proto_type=proto_type,
            is_repeated=is_repeated
        ))
    
    # Sort fields by number
    message.fields.sort(key=lambda f: f.number)
    
    return message if message.fields else None


def generate_proto_file(messages: list, enums: list, package: str = "rustplus") -> str:
    """Generate a .proto file from parsed messages and enums."""
    lines = [
        'syntax = "proto3";',
        '',
        f'package {package};',
        '',
    ]
    
    # Add common types that are referenced
    referenced_types = set()
    for msg in messages:
        for field in msg.fields:
            referenced_types.add(field.proto_type)
    
    # Add NetworkableId if referenced (it's a custom uint64 wrapper)
    if 'NetworkableId' in referenced_types:
        lines.extend([
            'message NetworkableId {',
            '  uint64 id = 1;',
            '}',
            '',
        ])
    
    # Add Vector types if needed
    if 'Vector2' in referenced_types:
        lines.extend([
            'message Vector2 {',
            '  float x = 1;',
            '  float y = 2;',
            '}',
            '',
        ])
    
    if 'Vector3' in referenced_types:
        lines.extend([
            'message Vector3 {',
            '  float x = 1;',
            '  float y = 2;',
            '  float z = 3;',
            '}',
            '',
        ])
    
    if 'Vector4' in referenced_types:
        lines.extend([
            'message Vector4 {',
            '  float x = 1;',
            '  float y = 2;',
            '  float z = 3;',
            '  float w = 4;',
            '}',
            '',
        ])
    
    if 'Color' in referenced_types:
        lines.extend([
            'message Color {',
            '  float r = 1;',
            '  float g = 2;',
            '  float b = 3;',
            '  float a = 4;',
            '}',
            '',
        ])
    
    # Add enums
    for enum in enums:
        lines.append(f'enum {enum.name} {{')
        for name, value in sorted(enum.values.items(), key=lambda x: x[1]):
            lines.append(f'  {name} = {value};')
        lines.append('}')
        lines.append('')
    
    # Add messages - deduplicate fields with same number
    for msg in messages:
        lines.append(f'message {msg.name} {{')
        
        # Group fields by number to detect duplicates
        fields_by_number = {}
        for field in msg.fields:
            if field.number not in fields_by_number:
                fields_by_number[field.number] = []
            fields_by_number[field.number].append(field)
        
        # Output fields, handling duplicates
        for field_num in sorted(fields_by_number.keys()):
            field_list = fields_by_number[field_num]
            if len(field_list) == 1:
                # Single field - normal case
                field = field_list[0]
                repeated = 'repeated ' if field.is_repeated else ''
                lines.append(f'  {repeated}{field.proto_type} {field.name} = {field.number};')
            else:
                # Multiple fields with same number - comment out duplicates
                for i, field in enumerate(field_list):
                    repeated = 'repeated ' if field.is_repeated else ''
                    if i == 0:
                        lines.append(f'  {repeated}{field.proto_type} {field.name} = {field.number};')
                    else:
                        lines.append(f'  // DUPLICATE: {repeated}{field.proto_type} {field.name} = {field.number};')
        
        lines.append('}')
        lines.append('')
    
    return '\n'.join(lines)


def main():
    if len(sys.argv) < 3:
        print("Usage: generate_proto.py <input_dir> <output_file>")
        sys.exit(1)
    
    input_dir = Path(sys.argv[1])
    output_file = Path(sys.argv[2])
    
    if not input_dir.exists():
        print(f"Error: Input directory {input_dir} does not exist")
        sys.exit(1)
    
    messages = []
    enums = []
    
    # Process ProtoBuf directory
    proto_dir = input_dir / "ProtoBuf"
    if proto_dir.exists():
        for cs_file in proto_dir.glob("*.cs"):
            print(f"Processing: {cs_file.name}")
            
            # Try parsing as enum first
            enum = parse_enum_file(cs_file)
            if enum:
                enums.append(enum)
                print(f"  Found enum: {enum.name} with {len(enum.values)} values")
                continue
            
            # Try parsing as message
            msg = parse_proto_class(cs_file)
            if msg:
                messages.append(msg)
                print(f"  Found message: {msg.name} with {len(msg.fields)} fields")
    
    print(f"\nTotal: {len(messages)} messages, {len(enums)} enums")
    
    # Generate proto file
    proto_content = generate_proto_file(messages, enums)
    
    output_file.parent.mkdir(parents=True, exist_ok=True)
    output_file.write_text(proto_content)
    
    print(f"\nGenerated: {output_file}")
    print(f"Size: {len(proto_content)} bytes")


if __name__ == "__main__":
    main()
